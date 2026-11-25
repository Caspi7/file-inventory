use std::{collections::BTreeMap,io::Read,sync::atomic::AtomicBool};
use rusqlite::{params,OptionalExtension};
use sha2::{Digest,Sha256};
use crate::{
    EngineResult,catalog::{Catalog,FileEntry,ENTRY_COLUMNS,file_entry,rows},
    content::safe_relative,content_extract::{open_safe,safe_source_path},scanner::{RECALL_RISK,check_cancel,file_key,timestamp},
    util::{hex,invalid,now_ms},
};

#[derive(Clone,Debug)]
pub struct DuplicateOptions { pub minimum_bytes:i64,pub include_offline:bool,pub include_stale:bool }
impl Default for DuplicateOptions {
    fn default()->Self {Self{minimum_bytes:1,include_offline:true,include_stale:true}}
}
#[derive(Clone,Debug)]
pub struct DuplicateGroup {
    pub bytes:i64,pub modified:i64,pub created:i64,pub files:Vec<FileEntry>,pub full_hash:Option<String>,
}

pub fn find_duplicate_groups(catalog:&Catalog,options:&DuplicateOptions)->EngineResult<Vec<DuplicateGroup>> {
    if options.minimum_bytes<0 {return Err(invalid("Minimum bytes must be nonnegative").into());}
    let db=catalog.open()?;
    let files=rows(&db,&format!("WITH candidates AS (
        SELECT f.bytes,f.modified,f.created FROM files f JOIN volumes v ON v.id=f.volume
        WHERE f.bytes>=?1 AND (f.attributes&16)=0 AND f.removed IS NULL AND (?2 OR v.online=1) AND (?3 OR f.stale=0)
        GROUP BY f.bytes,f.modified,f.created HAVING count(*)>1)
        SELECT {ENTRY_COLUMNS} FROM candidates c JOIN files f USING(bytes,modified,created)
        JOIN volumes v ON v.id=f.volume WHERE (f.attributes&16)=0 AND f.removed IS NULL
        AND (?2 OR v.online=1) AND (?3 OR f.stale=0)
        ORDER BY f.bytes DESC,f.modified,f.created,f.volume,f.rel"),
        params![options.minimum_bytes,options.include_offline,options.include_stale],file_entry)?;
    let mut groups:Vec<DuplicateGroup>=Vec::new();
    for file in files {
        let key=(file.bytes,file.modified,file.created);
        if let Some(group)=groups.last_mut().filter(|g|(g.bytes,g.modified,g.created)==key) {
            group.files.push(file);
        } else {
            groups.push(DuplicateGroup{bytes:file.bytes,modified:file.modified,created:file.created,files:vec![file],full_hash:None});
        }
    }
    Ok(groups)
}

pub fn verify_groups(catalog:&Catalog,groups:&[DuplicateGroup],cancel:&AtomicBool)->EngineResult<Vec<DuplicateGroup>> {
    let db=catalog.open()?;
    let mut verified=Vec::new();
    let mut buffer=vec![0;256<<10];
    for group in groups {
        let mut hashes:BTreeMap<Vec<u8>,Vec<FileEntry>>=BTreeMap::new();
        let mut seen=std::collections::HashSet::new();
        for candidate in &group.files {
            check_cancel(cancel)?;
            if !seen.insert(candidate.id) || !safe_relative(&candidate.rel) {continue;}
            let current=db.query_row(&format!("SELECT {ENTRY_COLUMNS} FROM files f JOIN volumes v ON v.id=f.volume
                WHERE f.id=?1 AND f.volume=?2 AND f.rel=?3 AND (f.bytes,f.modified,f.created)=(?4,?5,?6)
                AND v.online=1 AND v.mount=?7 AND f.stale=0 AND f.removed IS NULL AND (f.attributes&?8)=0"),
                params![candidate.id,candidate.volume,candidate.rel,group.bytes,group.modified,group.created,candidate.mount,RECALL_RISK|16],
                file_entry).optional()?;
            let Some(current)=current else {continue;};
            let Ok(path)=safe_source_path(&current.volume,&current.mount,&current.rel) else {continue;};
            let Ok(mut file)=open_safe(&path,current.bytes,current.modified) else {continue;};
            let Ok(metadata)=file.metadata() else {continue;};
            if timestamp(metadata.created())!=current.created {continue;}
            if current.file_key.is_some() && file_key(&path)!=current.file_key {continue;}
            let cached:Option<Vec<u8>>=db.query_row("SELECT full_hash FROM file_hashes
                WHERE file_id=?1 AND bytes=?2 AND modified=?3 AND length(full_hash)=32",
                params![current.id,current.bytes,current.modified],|r|r.get(0)).optional()?;
            let hash=if let Some(hash)=cached {hash} else {
                let mut hash=Sha256::new();
                let mut good=true;
                loop {
                    check_cancel(cancel)?;
                    match file.read(&mut buffer) {
                        Ok(0)=>break,
                        Ok(n)=>hash.update(&buffer[..n]),
                        Err(_)=>{good=false;break;},
                    }
                }
                if !good {continue;}
                let hash=hash.finalize().to_vec();
                let inserted=db.execute("INSERT INTO file_hashes(file_id,bytes,modified,full_hash,hashed_at)
                    SELECT f.id,f.bytes,f.modified,?1,?2 FROM files f JOIN volumes v ON v.id=f.volume
                    WHERE f.id=?3 AND f.volume=?4 AND f.rel=?5 AND (f.bytes,f.modified,f.created,f.seen,f.attributes)=(?6,?7,?8,?9,?10)
                    AND f.file_key IS ?11 AND f.stale=0 AND f.removed IS NULL AND v.online=1 AND v.mount=?12
                    ON CONFLICT(file_id) DO UPDATE SET bytes=excluded.bytes,modified=excluded.modified,
                    quick_hash=CASE WHEN (bytes,modified)=(excluded.bytes,excluded.modified) THEN quick_hash ELSE NULL END,
                    full_hash=excluded.full_hash,hashed_at=excluded.hashed_at",
                    params![hash,now_ms(),current.id,current.volume,current.rel,current.bytes,current.modified,
                        current.created,current.seen,current.attributes,current.file_key,current.mount])?;
                if inserted==0 {continue;}
                hash
            };
            hashes.entry(hash).or_default().push(current);
        }
        for (hash,files) in hashes {
            if files.len()>1 {
                verified.push(DuplicateGroup{bytes:group.bytes,modified:group.modified,created:group.created,
                    files,full_hash:Some(hex(&hash))});
            }
        }
    }
    Ok(verified)
}



