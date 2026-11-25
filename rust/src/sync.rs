use std::{
    collections::{HashMap,HashSet}, fs::{self,File}, io::{self,BufRead,BufReader,Read,Write,Seek},
    path::{Path,PathBuf}, sync::atomic::AtomicBool,
};
use aes_gcm::{Aes256Gcm, KeyInit, Nonce, Tag, aead::AeadInPlace};
use flate2::read::GzDecoder;
use rand::{rngs::OsRng,RngCore};
use rusqlite::{params,TransactionBehavior};
use serde::{Serialize,Deserialize};
use sha2::{Digest,Sha256};

use crate::{
    EngineResult, catalog::{Catalog,Volume,rebuild_folder_stats,rows},
    content::ContentIndex, scanner::{check_cancel,timestamp},
    util::{atomic_write,catalog_key,extension,hex,invalid,parent,prefixes,read_json,write_json},
};

const BROTLI_MAGIC:&[u8;7]=b"FINVBR1";
const ENCRYPTED_MAGIC:&[u8;8]=b"FINVENC1";
const PLAIN:&str=".fiinventory";
const LEGACY:&str=".finv.gz";
const ENCRYPTED:&str=".finv.enc";
const CONTENT:&str=".ficontent";

#[derive(Clone,Debug,Default,Serialize,Deserialize)]
#[serde(default,rename_all="PascalCase")]
pub struct CloudSyncConfig {
    pub folder:PathBuf,pub auto_sync_after_scan:bool,pub passphrase:String,pub sync_content_index:bool,
}
impl CloudSyncConfig {
    pub fn load(dir:&Path)->Self {
        read_json(&dir.join("sync.json")).or_else(|_|read_json(&dir.join("cloud_sync.json"))).unwrap_or_default()
    }
    pub fn save(&self,dir:&Path)->io::Result<()> {
        fs::create_dir_all(dir)?;
        write_json(&dir.join("sync.json"),self)?;
        write_json(&dir.join("cloud_sync.json"),self)
    }
}

#[derive(Clone,Debug)]
pub struct CloudProvider { pub name:String,pub root_path:PathBuf,pub suggested_folder:PathBuf }
#[derive(Clone,Debug,Default)]
pub struct SyncSummary { pub exported:usize,pub imported:usize,pub skipped:usize,pub errors:Vec<String> }

pub fn detect_providers()->Vec<CloudProvider> {
    let mut found=Vec::new();
    let mut seen=HashSet::new();
    let mut add=|name:&str,root:PathBuf| {
        if root.is_dir() && seen.insert(root.to_string_lossy().to_lowercase()) {
            found.push(CloudProvider{name:name.into(),suggested_folder:root.join("FileInventorySync"),root_path:root});
        }
    };
    #[cfg(windows)]
    {
        // A streamed Google Drive can mount on a drive classified as remote.
        let drives=unsafe {windows_sys::Win32::Storage::FileSystem::GetLogicalDrives()};
        for index in 0..26 {
            if drives&(1<<index)!=0 {add("Google Drive",PathBuf::from(format!("{}:\\My Drive",(b'A'+index) as char)));}
        }
    }
    for (name,var) in [("OneDrive","OneDrive"),("OneDrive (Work)","OneDriveCommercial"),("OneDrive (Personal)","OneDriveConsumer")] {
        if let Some(path)=std::env::var_os(var) {add(name,path.into());}
    }
    if let Some(home)=dirs::home_dir() {
        for (name,folder) in [("Google Drive","Google Drive"),("OneDrive","OneDrive"),("Dropbox","Dropbox"),("iCloud Drive","iCloudDrive")] {
            add(name,home.join(folder));
        }
    }
    found
}

#[derive(Serialize,Deserialize)]
#[serde(rename_all="PascalCase")]
struct Header {
    format:String,version:i32,id:String,label:String,mount:String,
    #[serde(default)] host:Option<String>,
    #[serde(default)] removable:bool,
    #[serde(default)] note:Option<String>,
}
type InventoryRow=(String,i64,i64,i64,i32,i64,i32,Option<i64>);

impl Catalog {
    pub fn export_inventory(&self,volume:&str,destination:&Path)->EngineResult<()> {
        export_inventory(self,volume,destination,&AtomicBool::new(false))
    }
    pub fn export_inventory_with_cancel(&self,volume:&str,destination:&Path,cancel:&AtomicBool)->EngineResult<()> {
        export_inventory(self,volume,destination,cancel)
    }
    pub fn import_inventory_with_passphrase(&self,source:&Path,passphrase:&str,cancel:&AtomicBool)->EngineResult<String> {
        let config=CloudSyncConfig {passphrase:passphrase.into(),..Default::default()};
        receive_snapshot(source,&config,cancel,|path|import_inventory(self,path,cancel))
    }
    pub fn import_inventory(&self,source:&Path)->EngineResult<String> {
        import_inventory(self,source,&AtomicBool::new(false))
    }
}

fn archive_id(origin:&str,volume:&str)->String {
    volume.strip_prefix("import:").map_or_else(||hex(&Sha256::digest(format!("{origin}{volume}"))),str::to_owned)
}

fn archive_aliases(catalog:&Catalog)->rusqlite::Result<HashMap<String,String>> {
    let db=catalog.open()?;
    db.execute("INSERT OR IGNORE INTO catalog_meta VALUES('archive-origin',lower(hex(randomblob(16))))",[])?;
    let entries=rows(&db,"SELECT id, (SELECT value FROM catalog_meta WHERE key='archive-origin')
        FROM volumes WHERE succeeded IS NOT NULL AND id NOT LIKE 'import:%'",[],|r| {
        let id:String=r.get(0)?;
        let origin:String=r.get(1)?;
        let alias=format!("import:{}",archive_id(&origin,&id));
        Ok((id,alias))
    })?;
    Ok(entries.into_iter().collect())
}
fn export_inventory(catalog:&Catalog,volume:&str,destination:&Path,cancel:&AtomicBool)->EngineResult<()> {
    let db=catalog.open()?;
    db.execute("INSERT OR IGNORE INTO catalog_meta VALUES('archive-origin',lower(hex(randomblob(16))))",[])?;
    db.execute_batch("BEGIN")?;
    let header=db.query_row("SELECT label,mount,host,removable,
        (SELECT value FROM catalog_meta WHERE key='archive-origin'),note FROM volumes
        WHERE id=?1 AND (succeeded IS NOT NULL OR id LIKE 'import:%')",[volume],|r|{
            let host:String=r.get(2)?;
            let origin:String=r.get(4)?;
            let id=archive_id(&origin,volume);
            Ok(Header {format:"FileInventory".into(),version:1,id,label:r.get(0)?,mount:r.get(1)?,
                host:Some(if host.trim().is_empty(){machine_name()}else{host}),removable:r.get(3)?,note:r.get(5)?})
        })?;
    let mut stmt=db.prepare("SELECT rel,bytes,created,modified,attributes,seen,stale,removed FROM files WHERE volume=?1 ORDER BY rel")?;
    let mut rows=stmt.query([volume])?;
    atomic_write(destination,|file|->EngineResult<()> {
        let mut write=|output:&mut dyn Write|->EngineResult<()> {
            serde_json::to_writer(&mut *output,&header)?;
            writeln!(output)?;
            let mut count=0u64;
            while let Some(r)=rows.next()? {
                check_cancel(cancel)?;
                let row:InventoryRow=(r.get(0)?,r.get(1)?,r.get(2)?,r.get(3)?,r.get(4)?,r.get(5)?,r.get(6)?,r.get(7)?);
                serde_json::to_writer(&mut *output,&row)?;
                writeln!(output)?;
                count+=1;
            }
            check_cancel(cancel)?;
            writeln!(output,"{count}")?;
            Ok(())
        };
        if destination.to_string_lossy().to_ascii_lowercase().ends_with(LEGACY) {
            let mut stream=flate2::write::GzEncoder::new(file,flate2::Compression::default());
            write(&mut stream)?;
            stream.finish()?;
        } else {
            file.write_all(BROTLI_MAGIC)?;
            write_brotli(file,write)?;
        }
        Ok(())
    })
}

fn import_inventory(catalog:&Catalog,source:&Path,cancel:&AtomicBool)->EngineResult<String> {
    let mut file=File::open(source)?;
    let mut magic=[0;7];
    file.read_exact(&mut magic)?;
    let decompressor:Box<dyn Read>=if &magic==BROTLI_MAGIC {
        Box::new(brotli::Decompressor::new(file,65536))
    } else {
        file.rewind()?;
        Box::new(GzDecoder::new(file))
    };
    let mut reader=BufReader::new(decompressor);
    let mut line=String::new();
    if !archive_line(&mut reader,&mut line)? {return Err(invalid("Missing inventory header").into());}
    let header:Header=serde_json::from_str(&line)?;
    let host=header.host.as_deref().unwrap_or("").trim();
    let mount=header.mount.as_bytes();
    if header.format!="FileInventory" || header.version!=1 || header.id.len()!=64 || !header.id.bytes().all(|c|c.is_ascii_hexdigit())
        || header.label.trim().is_empty() || header.label.encode_utf16().count()>512 || header.label.chars().any(char::is_control)
        || host.encode_utf16().count()>256 || host.chars().any(char::is_control)
        || mount.len()<3 || !mount[0].is_ascii_alphabetic() || &mount[1..3]!=b":\\"
        || header.mount.encode_utf16().count()>32767
        || (mount.len()>3 && !valid_archive_path(header.mount[3..].trim_end_matches('\\'))) {
        return Err(invalid("Invalid inventory archive header").into());
    }
    let id=format!("import:{}",header.id.to_ascii_uppercase());
    let mut db=catalog.open()?;
    let tx=db.transaction_with_behavior(TransactionBehavior::Immediate)?;
    tx.execute("INSERT INTO volumes(id,mount,label,online,outcome,host,removable,note)
        VALUES(?1,?2,?3,0,'Imported',?4,?5,coalesce(?6,''))
        ON CONFLICT(id) DO UPDATE SET mount=excluded.mount,label=excluded.label,online=0,host=excluded.host,
        removable=excluded.removable,note=coalesce(?6,volumes.note)",
        params![id,header.mount,header.label,host,header.removable,header.note])?;
    tx.execute("DELETE FROM files WHERE volume=?1",[&id])?;
    {
        let mut insert=tx.prepare("INSERT INTO files(volume,rel,name,name_key,parent_key,extension,bytes,created,modified,
           attributes,seen,stale,removed) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13)")?;
        let mut prefix=tx.prepare("INSERT INTO prefixes(token,file_id) SELECT value,last_insert_rowid() FROM json_each(?1)")?;
        let mut count=0u64;
        loop {
            check_cancel(cancel)?;
            if !archive_line(&mut reader,&mut line)? {return Err(invalid("Incomplete inventory archive").into());}
            if let Ok(total)=line.trim().parse::<u64>() {
                if total!=count || archive_line(&mut reader,&mut line)? {
                    return Err(invalid("Inventory entry count does not match").into());
                }
                break;
            }
            let (rel,bytes,created,modified,attrs,seen,stale,removed):InventoryRow=serde_json::from_str(&line)?;
            if !valid_archive_path(&rel) || bytes<0 || attrs<0 || !matches!(stale,0|1) ||
                [Some(created),Some(modified),Some(seen),removed].into_iter().flatten()
                    .any(|ms|!(-62_135_596_800_000..=253_402_300_799_999).contains(&ms)) {
                return Err(invalid("Invalid inventory record").into());
            }
            let name=rel.rsplit('\\').next().unwrap();
            let key=catalog_key(name);
            let parent=catalog_key(parent(&rel));
            insert.execute(params![id,rel,name,key,parent,extension(name,attrs&16!=0),bytes,created,modified,attrs,seen,stale,removed])?;
            prefix.execute([serde_json::to_string(&prefixes(&key,&parent))?])?;
            count+=1;
        }
    }
    rebuild_folder_stats(&tx,&id)?;
    check_cancel(cancel)?;
    tx.commit()?;
    Ok(id)
}

fn archive_line(reader:&mut impl BufRead,line:&mut String)->io::Result<bool> {
    line.clear();
    if reader.take(1_000_002).read_line(line)?==0 {return Ok(false);}
    if line.trim_end_matches('\n').encode_utf16().count()>250_000 {return Err(invalid("Inventory record too long"));}
    Ok(true)
}

fn valid_archive_path(path:&str)->bool {
    !path.is_empty() && path.encode_utf16().count()<=32767 && path.split('\\').all(|p|
        !p.is_empty() && p!="." && p!=".." && !p.chars().any(invalid_filename_char))
}
fn invalid_filename_char(c:char)->bool { c<' ' || "<>:\"/\\|?*".contains(c) }

pub fn encrypt_snapshot(source:&Path,destination:&Path,passphrase:&str)->EngineResult<()> {
    let mut data=fs::read(source)?;
    let mut salt=[0;16];
    let mut nonce=[0;12];
    OsRng.fill_bytes(&mut salt);
    OsRng.fill_bytes(&mut nonce);
    let cipher=cipher(passphrase,&salt);
    let tag=cipher.encrypt_in_place_detached(Nonce::from_slice(&nonce),ENCRYPTED_MAGIC,&mut data)
        .map_err(|_|invalid("Could not encrypt snapshot"))?;
    atomic_write(destination,|output| {
        output.write_all(ENCRYPTED_MAGIC)?;
        output.write_all(&salt)?;
        output.write_all(&nonce)?;
        output.write_all(&tag)?;
        output.write_all(&data)
    })?;
    Ok(())
}

pub fn decrypt_snapshot(source:&Path,destination:&Path,passphrase:&str)->EngineResult<()> {
    let mut data=fs::read(source)?;
    if data.len()<52 || &data[..8]!=ENCRYPTED_MAGIC {return Err(invalid("Invalid encrypted snapshot header").into());}
    let (header,body)=data.split_at_mut(52);
    cipher(passphrase,&header[8..24]).decrypt_in_place_detached(Nonce::from_slice(&header[24..36]),
        ENCRYPTED_MAGIC,body,Tag::from_slice(&header[36..52]))
        .map_err(|_|invalid("Could not decrypt snapshot. Check the sync passphrase"))?;
    atomic_write(destination,|output|output.write_all(body))?;
    Ok(())
}

fn cipher(passphrase:&str,salt:&[u8])->Aes256Gcm {
    let mut key=[0;32];
    pbkdf2::pbkdf2_hmac::<Sha256>(passphrase.as_bytes(),salt,100_000,&mut key);
    let cipher=Aes256Gcm::new((&key).into());
    use zeroize::Zeroize;
    key.zeroize();
    cipher
}

pub fn sync_inventory(catalog:&Catalog,config:&CloudSyncConfig,cancel:&AtomicBool)->EngineResult<SyncSummary> {
    sync_inventory_with_progress(catalog,config,cancel,|_,_,_|{})
}

pub fn sync_inventory_with_progress(catalog:&Catalog,config:&CloudSyncConfig,cancel:&AtomicBool,
    mut report:impl FnMut(usize,usize,&str))->EngineResult<SyncSummary> {
    prepare_sync(config,cancel)?;
    let mut summary=SyncSummary::default();
    let machine=machine_name();
    let local:Vec<_>=catalog.volumes()?.into_iter().filter(|v|!v.id.starts_with("import:") && v.succeeded.is_some()).collect();
    let mut own=HashSet::new();
    let mut own_short=HashSet::new();
    let incoming=snapshots(&config.folder,&[PLAIN,LEGACY,ENCRYPTED],cancel)?;
    let total=local.len()+incoming.len();
    for (done,volume) in local.iter().enumerate() {
        report(done,total,&volume.mount);
        check_cancel(cancel)?;
        let base=snapshot_base_name(&machine,volume);
        let old=format!("{}__{}",legacy_part(&machine),legacy_part(&volume.id));
        own.insert(base.to_lowercase());
        own.insert(old.to_lowercase());
        own_short.insert(short_id(&volume.id).to_ascii_lowercase());
        let encrypted=!config.passphrase.is_empty();
        let destination=config.folder.join(format!("{base}{}",if encrypted{ENCRYPTED}else{PLAIN}));
        if modified(&destination).is_some_and(|at|Some(at)>=volume.succeeded) {summary.skipped+=1;continue;}
        let result=publish_snapshot(&destination,config,cancel,|path|export_inventory(catalog,&volume.id,path,cancel));
        match result {
            Ok(())=>{
                summary.exported+=1;
                for (stem,ext) in [(&base,if encrypted{PLAIN}else{ENCRYPTED}),(&base,LEGACY),(&old,LEGACY),(&old,PLAIN),(&old,ENCRYPTED)] {
                    let path=config.folder.join(format!("{stem}{ext}"));
                    if path!=destination { let _=fs::remove_file(path); }
                }
            }
            Err(e)=>summary.errors.push(format!("Export {}: {e}",volume.label)),
        }
    }
    let state_path=catalog.data_dir.join("rust-sync-state.json");
    let mut state:HashMap<String,(i64,u64,String)>=read_json(&state_path).unwrap_or_default();
    let mut volumes:HashSet<_>=catalog.volumes()?.into_iter().map(|v|v.id).collect();
    let machine_prefix=format!("{} - ",readable(&machine,64,"PC")).to_lowercase();
    for (i,entry) in incoming.into_iter().enumerate() {
        check_cancel(cancel)?;
        report(local.len()+i,total,&entry.file_name().to_string_lossy());
        let name=entry.file_name().to_string_lossy().to_lowercase();
        let Some(stem)=[PLAIN,LEGACY,ENCRYPTED].iter().find_map(|ext|name.strip_suffix(ext)) else {continue;};
        if own.contains(stem) || (stem.starts_with(&machine_prefix) && stem.strip_suffix(']')
            .and_then(|s|s.rsplit_once('[')).is_some_and(|(_,id)|own_short.contains(id))) {
            summary.skipped+=1;
            continue;
        }
        let meta=entry.metadata()?;
        let stamp=timestamp(meta.modified());
        if state.get(&name).is_some_and(|(at,len,id)|*at==stamp && *len==meta.len() && volumes.contains(id)) {
            summary.skipped+=1;
            continue;
        }
        match receive_snapshot(&entry.path(),config,cancel,|path|import_inventory(catalog,path,cancel)) {
            Ok(id)=>{
                volumes.insert(id.clone());
                state.insert(name,(stamp,meta.len(),id));
                summary.imported+=1;
            }
            Err(e)=>summary.errors.push(format!("Import {}: {e}",entry.path().display())),
        }
    }
    check_cancel(cancel)?;
    write_json(&state_path,&state)?;
    report(total,total,"Complete");
    Ok(summary)
}

pub fn sync_content(index:&ContentIndex,config:&CloudSyncConfig,cancel:&AtomicBool)->EngineResult<SyncSummary> {
    sync_content_with_progress(index,config,cancel,|_,_,_|{})
}

pub fn sync_content_with_progress(index:&ContentIndex,config:&CloudSyncConfig,cancel:&AtomicBool,
    mut report:impl FnMut(usize,usize,&str))->EngineResult<SyncSummary> {
    prepare_sync(config,cancel)?;
    let mut summary=SyncSummary::default();
    let own=format!("{} - ContentIndex{CONTENT}",machine_name());
    let incoming=snapshots(&config.folder,&[CONTENT],cancel)?;
    let total=1+incoming.len();
    report(0,total,&own);
    if index.stats()?.indexed_files==0 {summary.skipped+=1;}
    else {
        let aliases=archive_aliases(&index.catalog)?;
        match publish_snapshot(&config.folder.join(&own),config,cancel,
            |path|index.export_with_cancel(path,None,cancel,&aliases)) {
            Ok(())=>summary.exported+=1,
            Err(e)=>summary.errors.push(format!("Export content: {e}")),
        }
    }
    for (i,entry) in incoming.into_iter().enumerate() {
        check_cancel(cancel)?;
        let name=entry.file_name();
        let name=name.to_string_lossy();
        report(i+1,total,&name);
        if name.eq_ignore_ascii_case(&own) {continue;}
        match receive_snapshot(&entry.path(),config,cancel,|path|index.import_with_cancel(path,cancel)) {
            Ok(0)=>summary.skipped+=1,
            Ok(_)=>summary.imported+=1,
            Err(e)=>summary.errors.push(format!("Import {name}: {e}")),
        }
    }
    check_cancel(cancel)?;
    report(total,total,"Complete");
    Ok(summary)
}

fn snapshots(folder:&Path,extensions:&[&str],cancel:&AtomicBool)->EngineResult<Vec<fs::DirEntry>> {
    let mut snapshots=Vec::new();
    for entry in fs::read_dir(folder)? {
        check_cancel(cancel)?;
        let entry=entry?;
        let name=entry.file_name().to_string_lossy().to_ascii_lowercase();
        if extensions.iter().any(|ext|name.ends_with(ext)) && entry.file_type()?.is_file() {
            snapshots.push(entry);
        }
    }
    Ok(snapshots)
}

fn prepare_sync(config:&CloudSyncConfig,cancel:&AtomicBool)->io::Result<()> {
    check_cancel(cancel)?;
    if config.folder.as_os_str().is_empty() {return Err(invalid("Choose a cloud sync folder first"));}
    fs::create_dir_all(&config.folder)
}
fn receive_snapshot<T>(source:&Path,config:&CloudSyncConfig,cancel:&AtomicBool,read:impl FnOnce(&Path)->EngineResult<T>)->EngineResult<T> {
    let mut magic=[0;8];
    let encrypted=File::open(source)?.read_exact(&mut magic).is_ok() && &magic==ENCRYPTED_MAGIC;
    if encrypted {
        if config.passphrase.is_empty() {return Err(invalid("An encrypted snapshot requires a passphrase").into());}
        let (plain,handle)=crate::util::ScratchFile::create(&std::env::temp_dir(),"finv-",".tmp")?; drop(handle);
        decrypt_snapshot(source,plain.path(),&config.passphrase)?;
        check_cancel(cancel)?;
        read(plain.path())
    } else {read(source)}
}
fn publish_snapshot(destination:&Path,config:&CloudSyncConfig,cancel:&AtomicBool,write:impl FnOnce(&Path)->EngineResult<()>)->EngineResult<()> {
    if config.passphrase.is_empty() {return write(destination);}
    let (plain,handle)=crate::util::ScratchFile::create(&std::env::temp_dir(),"finv-",".tmp")?; drop(handle);
    write(plain.path())?;
    check_cancel(cancel)?;
    encrypt_snapshot(plain.path(),destination,&config.passphrase)
}

fn modified(path:&Path)->Option<i64> {fs::metadata(path).ok()?.modified().ok().map(|at|timestamp(Ok(at)))}
fn machine_name()->String {std::env::var("COMPUTERNAME").or_else(|_|std::env::var("HOSTNAME")).unwrap_or_else(|_|"PC".into())}
pub fn snapshot_base_name(machine:&str,volume:&Volume)->String {
    let name=if volume.label.eq_ignore_ascii_case(&volume.mount) {"Drive".into()} else {readable(&volume.label,150,"Drive")};
    let letter=match volume.mount.as_bytes() {
        [first,b':',..] if first.is_ascii_alphabetic()=>format!(" ({})",first.to_ascii_uppercase() as char),
        _=>String::new(),
    };
    format!("{} - {name}{letter} [{}]",readable(machine,64,"PC"),short_id(&volume.id))
}
fn readable(value:&str,max:usize,fallback:&str)->String {
    let value:String=value.chars().map(|c|if invalid_filename_char(c){'-'}else{c}).collect();
    let mut units=0;
    let value:String=value.trim().chars().take_while(|c|{units+=c.len_utf16();units<=max}).collect();
    let value=value.trim_end_matches(['.',' ']);
    if value.is_empty(){fallback.into()}else{value.into()}
}
fn short_id(id:&str)->String {
    let identifier=id.split_once('{').map(|(_,tail)|tail)
        .or_else(||id.get(..7).filter(|p|p.eq_ignore_ascii_case("serial:")).map(|_|&id[7..])).unwrap_or(id);
    let short:String=identifier.chars().filter(char::is_ascii_alphanumeric).take(6).collect();
    if short.len()==6 {short} else {hex(&Sha256::digest(id))[..6].into()}
}
fn legacy_part(value:&str)->String {
    let mut cleaned=String::new();
    for c in value.chars() {
        if c.is_ascii_alphanumeric() || c=='-' {cleaned.push(c);}
        else if !cleaned.is_empty() && !cleaned.ends_with('_') {cleaned.push('_');}
    }
    let cleaned=cleaned.trim_end_matches('_');
    if (1..=48).contains(&cleaned.len()) {return cleaned.into();}
    let hash=hex(&Sha256::digest(value))[..12].to_ascii_lowercase();
    if cleaned.is_empty(){hash}else{format!("{}_{hash}",cleaned[..35].trim_end_matches('_'))}
}

// CompressorWriter finishes inside into_inner, which cannot return a write error.
pub(crate) fn write_brotli<T>(file:&mut File,write:impl FnOnce(&mut dyn Write)->EngineResult<T>)->EngineResult<T> {
    struct Output<'a> {file:&'a mut File,error:Option<io::Error>}
    impl Write for Output<'_> {
        fn write(&mut self,bytes:&[u8])->io::Result<usize> {
            self.file.write(bytes).inspect_err(|e|self.error=Some(io::Error::new(e.kind(),e.to_string())))
        }
        fn flush(&mut self)->io::Result<()> {
            self.file.flush().inspect_err(|e|self.error=Some(io::Error::new(e.kind(),e.to_string())))
        }
    }
    let mut output=brotli::CompressorWriter::new(Output{file,error:None},65536,5,22);
    let result=write(&mut output)?;
    let output=output.into_inner();
    if let Some(error)=output.error {return Err(error.into());}
    Ok(result)
}




