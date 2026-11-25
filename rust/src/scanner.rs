use std::{
    fs, io, path::{Path, PathBuf}, sync::{Arc, atomic::{AtomicBool, Ordering}},
    thread, time::{Instant, SystemTime, UNIX_EPOCH},
};
use crossbeam_channel::{bounded, unbounded};
use rusqlite::{params, Batch, Connection, OptionalExtension, TransactionBehavior};

use crate::{
    EngineResult, catalog::{Catalog, PUBLISH, Volume, rebuild_folder_stats, upsert_mounted},
    util::{catalog_key, extension, interrupted, invalid, now_ms, prefixes},
};

pub(crate) const DIRECTORY: u32 = 0x10;
pub(crate) const RECALL_RISK: u32 = 0x441400;

#[derive(Clone, Debug, Default)]
pub struct ScanProgress {
    pub volume_id: String,
    pub items: u64,
    pub folders: u64,
    pub estimated_total: Option<u64>,
    pub current_dir: String,
    pub elapsed_ms: u64,
    pub omitted: u64,
}

#[derive(Clone, Debug)]
pub struct ScanOutcome {
    pub scan_id: i64,
    pub outcome: String,
    pub items: u64,
    pub folders: u64,
    pub omitted: u64,
}

struct Entry {
    name: String, bytes: i64, modified: i64, created: i64, attributes: u32, key: Option<String>,
}

enum Observation {
    Entry(String, Entry),
    Coverage(String, Option<String>),
}

pub fn scan(
    catalog: &Catalog,
    volume: &Volume,
    cancel: Arc<AtomicBool>,
    mut report: impl FnMut(&ScanProgress),
) -> EngineResult<ScanOutcome> {
    check_cancel(&cancel)?;
    let root = fs::canonicalize(&volume.mount)?;
    verify_mount(&volume.id,&volume.mount)?;
    let root_meta = fs::symlink_metadata(&root)?;
    if !root_meta.is_dir() || attributes(&root_meta) & RECALL_RISK != 0 {
        return Err(invalid("Scan root must be an accessible directory").into());
    }
    fs::create_dir_all(&catalog.data_dir)?;
    let excluded = fs::canonicalize(&catalog.data_dir)?;
    if excluded == root { return Err(invalid("Scan root is the catalog data directory").into()); }

    let (stage_file, stage_handle) = crate::util::ScratchFile::create(&catalog.data_dir, "scan-", ".sqlite3")?;
    drop(stage_handle);
    let mut db = catalog.open()?;
    let started = now_ms();
    let clock = Instant::now();
    let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let active: bool = tx.query_row("SELECT EXISTS(SELECT 1 FROM scans WHERE volume=?1 AND finished IS NULL)",
        [&volume.id], |r| r.get(0))?;
    if active { return Err(io::Error::new(io::ErrorKind::AlreadyExists, "Volume is already scanning").into()); }
    upsert_mounted(&tx, volume)?;
    let estimated_total = tx.query_row("SELECT items FROM scans WHERE volume=?1 AND finished IS NOT NULL
      AND outcome IN ('Complete','Complete with omissions') AND items>0 ORDER BY id DESC LIMIT 1",
      [&volume.id], |r|r.get(0)).optional()?;
    tx.execute("INSERT INTO scans(volume,started,outcome) VALUES(?1,?2,'Scanning')", params![volume.id,started])?;
    let scan_id = tx.last_insert_rowid();
    tx.execute("UPDATE volumes SET attempted=?2,outcome='Scanning',issues='' WHERE id=?1",params![volume.id,started])?;
    tx.commit()?;

    let mut progress = ScanProgress {
        volume_id:volume.id.clone(), estimated_total, current_dir:volume.mount.clone(),
        ..ScanProgress::default()
    };
    let mut omitted = 0;
    let mut issues = Vec::new();
    let result = (|| -> EngineResult<String> {
        report(&progress);
        check_cancel(&cancel)?;
        let stage = Connection::open(stage_file.path())?;
        stage.execute_batch("PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;
            PRAGMA locking_mode=EXCLUSIVE; PRAGMA cache_size=-131072;
            CREATE TABLE entries(rel TEXT PRIMARY KEY,name TEXT,name_key TEXT,parent_key TEXT,extension TEXT,
              bytes INTEGER,modified INTEGER,created INTEGER,attributes INTEGER,file_key TEXT,tokens TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE coverage(directory TEXT PRIMARY KEY,complete INTEGER,detail TEXT) WITHOUT ROWID;
            CREATE INDEX coverage_status ON coverage(complete,directory);
            BEGIN IMMEDIATE;")?;
        let native = native_serial(&root);
        let worker_count = thread::available_parallelism().map_or(2, usize::from).clamp(1,32);
        thread::scope(|scope| -> EngineResult<()> {
            let (dirs_tx, dirs_rx) = unbounded::<(String, PathBuf)>();
            let (results_tx, results_rx) = bounded(4096);
            for _ in 0..worker_count {
                let dirs = dirs_rx.clone();
                let results = results_tx.clone();
                let cancel = &cancel;
                scope.spawn(move || {
                    let mut buffer = vec![0u64;8192];
                    while let Ok((rel, path)) = dirs.recv() {
                        if cancel.load(Ordering::Relaxed) { break; }
                        let result = enumerate(&path,native,&mut buffer,cancel,|entry| {
                            results.send(Observation::Entry(rel.clone(),entry)).map_err(|_|interrupted())
                        });
                        if results.send(Observation::Coverage(rel,result.err().map(|e|e.to_string()))).is_err() { break; }
                    }
                });
            }
            drop(results_tx);
            dirs_tx.send((String::new(),root.clone()))?;
            let mut outstanding = 1;
            let mut insert = stage.prepare("INSERT INTO entries VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)")?;
            let mut cover = stage.prepare("INSERT OR REPLACE INTO coverage VALUES(?1,?2,?3)")?;
            let mut last_report = 0;
            while outstanding>0 {
                check_cancel(&cancel)?;
                match results_rx.recv()? {
                    Observation::Entry(parent, entry) => {
                        let rel = if parent.is_empty() { entry.name.clone() } else { format!("{parent}\\{}",entry.name) };
                        let directory = entry.attributes & DIRECTORY != 0;
                        if directory {
                            let path = crate::util::local_path(&root.to_string_lossy(),&rel);
                            if same_path(&path,&excluded) { continue; }
                            progress.folders+=1;
                            if entry.attributes & RECALL_RISK == 0 {
                                dirs_tx.send((rel.clone(),path))?;
                                outstanding+=1;
                            } else {
                                cover.execute(params![rel,false,"Reparse point or recall-risk directory"])?;
                                omitted+=1;
                            }
                        }
                        let name_key=catalog_key(&entry.name);
                        let parent_key=catalog_key(&parent);
                        let tokens=serde_json::to_string(&prefixes(&name_key,&parent_key))?;
                        insert.execute(params![rel,entry.name,name_key,parent_key,extension(&entry.name,directory),
                            entry.bytes,entry.modified,entry.created,entry.attributes,entry.key,tokens])?;
                        progress.items+=1;
                    }
                    Observation::Coverage(rel, detail) => {
                        if let Some(ref detail)=detail {
                            if rel.is_empty() { return Err(invalid(format!("Unable to enumerate scan root: {detail}")).into()); }
                            omitted+=1;
                            if issues.len()<40 { issues.push(format!("{rel}: {detail}")); }
                        }
                        cover.execute(params![rel,detail.is_none(),detail])?;
                        progress.current_dir=crate::util::local_path(&volume.mount,&rel).to_string_lossy().into_owned();
                        outstanding-=1;
                    }
                }
                progress.elapsed_ms=clock.elapsed().as_millis() as u64;
                progress.omitted=omitted;
                if progress.elapsed_ms.saturating_sub(last_report)>=300 {
                    report(&progress);
                    last_report=progress.elapsed_ms;
                }
            }
            Ok(())
        })?;
        check_cancel(&cancel)?;
        stage.execute_batch("COMMIT")?;
        drop(stage);
        verify_mount(&volume.id,&volume.mount)?;
        db.execute("ATTACH DATABASE ?1 AS pending", [stage_file.path().to_string_lossy().as_ref()])?;
        let cancel_query = Arc::clone(&cancel);
        db.progress_handler(8_000, Some(move ||cancel_query.load(Ordering::Relaxed)));
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let prior:bool = tx.query_row("SELECT succeeded IS NOT NULL FROM volumes WHERE id=?1",[&volume.id],|r|r.get(0))?;
        let finished=now_ms();
        let mut batch = Batch::new(&tx,PUBLISH);
        while let Some(mut statement)=batch.next()? {
            check_cancel(&cancel)?;
            for i in 1..=statement.parameter_count() {
                match statement.parameter_name(i) {
                    Some("$volume")=>statement.raw_bind_parameter(i,&volume.id)?,
                    Some("$scan")=>statement.raw_bind_parameter(i,scan_id)?,
                    Some("$seen")=>statement.raw_bind_parameter(i,finished)?,
                    Some("$hasPriorScan")=>statement.raw_bind_parameter(i,prior)?,
                    _=>return Err(invalid("Unexpected Publish.sql parameter").into()),
                }
            }
            statement.raw_execute()?;
        }
        rebuild_folder_stats(&tx,&volume.id)?;
        verify_mount(&volume.id,&volume.mount)?;
        check_cancel(&cancel)?;
        let outcome=if omitted==0 { "Complete" } else { "Complete with omissions" };
        record(&tx,scan_id,&volume.id,outcome,progress.items,&issues.join("\n"),true)?;
        tx.commit()?;
        Ok(outcome.into())
    })();
    db.progress_handler(0,None::<fn()->bool>);
    let outcome = match result {
        Ok(outcome) => {
            let _ = db.execute_batch("DETACH DATABASE pending; PRAGMA incremental_vacuum(256); PRAGMA optimize;");
            progress.elapsed_ms = clock.elapsed().as_millis() as u64;
            progress.omitted = omitted;
            report(&progress);
            outcome
        }
        Err(error) => {
            let cancelled = cancel.load(Ordering::Relaxed);
            let outcome = if cancelled { "Cancelled" } else { "Failed" };
            let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
            record(&tx,scan_id,&volume.id,outcome,progress.items,&error.to_string(),false)?;
            tx.commit()?;
            if cancelled { return Err(interrupted().into()); }
            return Err(error);
        }
    };
    Ok(ScanOutcome { scan_id,outcome,items:progress.items,folders:progress.folders,omitted })
}

fn record(db: &Connection, scan: i64, volume: &str, outcome: &str, items: u64, issues: &str, success: bool) -> rusqlite::Result<()> {
    let finished = now_ms();
    db.execute("UPDATE scans SET finished=?2,outcome=?3,items=?4,issues=?5 WHERE id=?1",params![scan,finished,outcome,items,issues])?;
    db.execute("UPDATE volumes SET succeeded=CASE WHEN ?2 THEN ?3 ELSE succeeded END,outcome=?4,issues=?5 WHERE id=?1",
        params![volume,success,finished,outcome,issues])?;
    for (table, key) in [("scan_changes","scan_id"),("scan_coverage","scan_id"),("moves","scan_id"),("scans","id")] {
        db.execute(&format!("DELETE FROM {table} WHERE {key} IN (SELECT id FROM scans WHERE volume=?1 AND finished IS NOT NULL ORDER BY id DESC LIMIT -1 OFFSET 20)"),[volume])?;
    }
    Ok(())
}

fn same_path(a: &Path, b: &Path) -> bool {
    #[cfg(windows)]
    { a.to_string_lossy().eq_ignore_ascii_case(&b.to_string_lossy()) }
    #[cfg(not(windows))]
    { a == b }
}

pub(crate) fn verify_mount(id:&str,mount:&str) -> io::Result<()> {
    #[cfg(windows)]
    if (id.starts_with(r"\\?\Volume{") || id.starts_with("serial:"))
        && !crate::catalog::windows_volume(mount).is_some_and(|v|v.0==id) {
        return Err(invalid("The selected volume was disconnected or remounted"));
    }
    let _ = (id,mount);
    Ok(())
}
pub(crate) fn check_cancel(cancel: &AtomicBool) -> io::Result<()> {
    if cancel.load(Ordering::Relaxed) { Err(interrupted()) } else { Ok(()) }
}

pub(crate) fn timestamp(time: io::Result<SystemTime>) -> i64 {
    match time {
        Ok(time) => match time.duration_since(UNIX_EPOCH) {
            Ok(duration)=>duration.as_millis() as i64,
            Err(error)=>-(error.duration().as_nanos().div_ceil(1_000_000) as i64),
        },
        Err(_)=>0,
    }
}

pub(crate) fn attributes(meta: &fs::Metadata) -> u32 {
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        meta.file_attributes()
    }
    #[cfg(not(windows))] {
        if meta.is_dir() { DIRECTORY } else if meta.is_symlink() { 0x400 } else { 0 }
    }
}

fn enumerate(path: &Path, serial: Option<u64>, buffer: &mut [u64], cancel: &AtomicBool, mut emit: impl FnMut(Entry)->io::Result<()>) -> io::Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.is_dir() || attributes(&metadata)&RECALL_RISK!=0 {
        return Err(invalid("Directory changed to a reparse point or recall risk"));
    }
    #[cfg(windows)]
    if let Some(serial)=serial {
        if native::enumerate(path,serial,buffer,cancel,&mut emit)? { return Ok(()); }
    }
    #[cfg(not(windows))]
    let _ = (serial,buffer);
    let mut failure = None;
    for entry in fs::read_dir(path)? {
        check_cancel(cancel)?;
        let info = entry.and_then(|entry| {
            let meta = fs::symlink_metadata(entry.path())?;
            let attrs = attributes(&meta);
            let name = entry.file_name().into_string().map_err(|_|invalid("Invalid UTF-16 filename"))?;
            let key = if attrs & (DIRECTORY|RECALL_RISK)==0 { file_key(&entry.path()) } else { None };
            Ok(Entry { name,bytes:if attrs & DIRECTORY != 0 { 0 } else { meta.len() as i64 },
                modified:timestamp(meta.modified()), created:timestamp(meta.created()), attributes:attrs, key })
        });
        match info {
            Ok(entry)=>emit(entry)?,
            Err(error)=>failure=Some(error),
        }
    }
    failure.map_or(Ok(()),Err)
}

#[cfg(windows)]
fn native_serial(path: &Path) -> Option<u64> { native::serial(path) }
#[cfg(not(windows))]
fn native_serial(_: &Path) -> Option<u64> { None }

#[cfg(windows)]
pub(crate) fn file_key(path: &Path) -> Option<String> { native::key(path) }
#[cfg(not(windows))]
pub(crate) fn file_key(_: &Path) -> Option<String> { None }

#[cfg(windows)]
mod native {
    use super::*;
    use std::{os::windows::{ffi::OsStrExt, io::{AsRawHandle, FromRawHandle, OwnedHandle}}, ptr::{null, null_mut}};
    use windows_sys::{
        Wdk::Storage::FileSystem::{NtQueryDirectoryFile, FileIdExtdDirectoryInformation},
        Win32::{Foundation::INVALID_HANDLE_VALUE, Storage::FileSystem::*, System::IO::IO_STATUS_BLOCK},
    };

    fn open(path: &Path, access: u32, flags: u32) -> io::Result<OwnedHandle> {
        let path:Vec<_> = path.as_os_str().encode_wide().chain([0]).collect();
        // CreateFileW returns a unique handle on success.
        unsafe {
            let raw=CreateFileW(path.as_ptr(),access,7,null(),OPEN_EXISTING,flags,null_mut());
            if raw==INVALID_HANDLE_VALUE { Err(io::Error::last_os_error()) }
            else { Ok(OwnedHandle::from_raw_handle(raw)) }
        }
    }

    fn id(path: &Path, flags: u32) -> Option<FILE_ID_INFO> {
        let file = open(path, FILE_READ_ATTRIBUTES, flags).ok()?;
        // FILE_ID_INFO is initialized by the API on success.
        unsafe {
            let mut info=std::mem::zeroed::<FILE_ID_INFO>();
            (GetFileInformationByHandleEx(file.as_raw_handle(), FileIdInfo, (&mut info as *mut FILE_ID_INFO).cast(),
                std::mem::size_of::<FILE_ID_INFO>() as u32)!=0).then_some(info)
        }
    }

    pub(super) fn serial(path: &Path) -> Option<u64> {
        let handle = open(path,FILE_READ_ATTRIBUTES,FILE_FLAG_BACKUP_SEMANTICS).ok()?;
        let mut fs = [0u16;16];
        // The filesystem-name buffer is independent of optional output arguments.
        let ok = unsafe { GetVolumeInformationByHandleW(handle.as_raw_handle(),null_mut(),0,null_mut(),
            null_mut(),null_mut(),fs.as_mut_ptr(),fs.len() as u32) };
        if ok==0 || fs[..5]!=[78,84,70,83,0] { return None; }
        id(path,FILE_FLAG_BACKUP_SEMANTICS).map(|info|info.VolumeSerialNumber)
    }

    pub(super) fn key(path: &Path) -> Option<String> {
        let info=id(path,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OPEN_NO_RECALL)?;
        let [low,high]=id_parts(&info.FileId.Identifier);
        Some(format!("{:016X}:{low:016X}{high:016X}",info.VolumeSerialNumber))
    }

    fn id_parts(data:&[u8])->[u64;2] {
        [u64::from_le_bytes(data[..8].try_into().unwrap()),u64::from_le_bytes(data[8..16].try_into().unwrap())]
    }

    pub(super) fn enumerate(path:&Path,serial:u64,buffer:&mut[u64],cancel:&AtomicBool,emit:&mut impl FnMut(Entry)->io::Result<()>) -> io::Result<bool> {
        let handle=open(path,FILE_LIST_DIRECTORY|0x00100000,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OPEN_NO_RECALL)?;
        // The u64 backing allocation supplies the alignment required for the kernel structure.
        let bytes=unsafe { std::slice::from_raw_parts_mut(buffer.as_mut_ptr().cast::<u8>(),std::mem::size_of_val(buffer)) };
        let mut first=true;
        loop {
            check_cancel(cancel)?;
            // A synchronous directory handle keeps the buffer and IO status valid until the call returns.
            let (status, used)=unsafe {
                let mut io=std::mem::zeroed::<IO_STATUS_BLOCK>();
                let status=NtQueryDirectoryFile(handle.as_raw_handle(),null_mut(),None,null(),&mut io,
                    bytes.as_mut_ptr().cast(),bytes.len() as u32, FileIdExtdDirectoryInformation,0,null(),u8::from(first));
                (status as u32,io.Information)
            };
            match status {
                0x80000006|0xC000000F=>return Ok(true),
                0xC0000003|0xC00000BB|0xC0000002|0xC000000D if first=>return Ok(false),
                0=>{},
                _=>return Err(io::Error::other(format!("NtQueryDirectoryFile returned 0x{status:08X}"))),
            }
            first=false;
            let mut batch=bytes.get(..used).ok_or_else(||invalid("Invalid directory buffer size"))?;
            while !batch.is_empty() {
                let header=batch.get(..88).ok_or_else(||invalid("Truncated directory record"))?;
                let u32_at=|offset|u32::from_le_bytes(header[offset..offset+4].try_into().unwrap());
                let i64_at=|offset|i64::from_le_bytes(header[offset..offset+8].try_into().unwrap());
                let next=u32_at(0) as usize;
                let length=u32_at(60) as usize;
                let record=if next==0 {batch} else {batch.get(..next).ok_or_else(||invalid("Invalid directory record offset"))?};
                let name_bytes=record.get(88..88+length).filter(|s|s.len()%2==0).ok_or_else(||invalid("Invalid directory name length"))?;
                let wide:Vec<_>=name_bytes.chunks_exact(2).map(|c|u16::from_le_bytes([c[0],c[1]])).collect();
                if wide!=[46] && wide!=[46,46] {
                    let name=String::from_utf16(&wide).map_err(invalid_utf16)?;
                    let attributes=u32_at(56);
                    let [low,high]=id_parts(&header[72..88]);
                    let key=(attributes & (DIRECTORY|RECALL_RISK)==0 && (low!=0 || high!=0))
                        .then(||format!("{serial:016X}:{low:016X}{high:016X}"));
                    emit(Entry { name,attributes,key,bytes:if attributes&DIRECTORY!=0 {0} else {i64_at(40)},
                        created:filetime(i64_at(8)),modified:filetime(i64_at(24)) })?;
                }
                if next==0 { break; }
                batch=&batch[next..];
            }
            if used==0 { return Ok(true); }
        }
    }

    fn invalid_utf16(_: std::string::FromUtf16Error) -> io::Error { invalid("Invalid UTF-16 filename") }
    fn filetime(value:i64)->i64 { value.div_euclid(10_000)-11_644_473_600_000 }
}





