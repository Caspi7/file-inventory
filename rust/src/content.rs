use std::{
    collections::{HashMap, HashSet}, fs::{File,self}, io::{self,Read,Write}, path::{Path,PathBuf},
    sync::{Arc,atomic::AtomicBool}, thread,
};
use crossbeam_channel::bounded;
use rusqlite::{Connection, OptionalExtension, TransactionBehavior, named_params, params};

use crate::{
    catalog::{Catalog, configure, rows, sql_limit},
    content_extract::{self, OFFICE, TEXT},
    content_query::{self, Node, quote},
    scanner::{RECALL_RISK,check_cancel},
    util::{atomic_write, invalid, local_path, normalize_key, now_ms, words},
    EngineResult,
};

pub const SCHEMA: &str = "
PRAGMA auto_vacuum=INCREMENTAL;
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS content_files(
    file_id INTEGER PRIMARY KEY, volume TEXT NOT NULL, rel TEXT NOT NULL,
    bytes INTEGER NOT NULL, modified INTEGER NOT NULL, extractor_ver INTEGER NOT NULL,
    status INTEGER NOT NULL, indexed_at INTEGER NOT NULL, brotli_text BLOB);
CREATE INDEX IF NOT EXISTS content_files_vol_rel ON content_files(volume, rel);
CREATE TABLE IF NOT EXISTS content_sizes(
    file_id INTEGER PRIMARY KEY, text_bytes INTEGER NOT NULL, text_cap INTEGER NOT NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS content_fts
USING fts5(body, content='', contentless_delete=1, tokenize='porter unicode61 remove_diacritics 2');";

const MAX_FIELD:usize=64<<20;
const EXTRACTOR:i32=1;

#[derive(Copy,Clone,Debug,Default,Eq,PartialEq)]
pub enum ContentScanMode { #[default] Eco, Balanced, Turbo }

#[derive(Clone,Debug)]
pub struct ContentScanOptions {
    pub volume_ids:Option<Vec<String>>, pub mode:ContentScanMode, pub max_file_bytes:i64,
    pub max_text_chars:usize, pub store_snippets:bool, pub include_text:bool,
    pub include_office:bool, pub include_pdf:bool, pub skip_system_folders:bool,
}
impl Default for ContentScanOptions {
    fn default()->Self {
        Self { volume_ids:None,mode:ContentScanMode::Eco,max_file_bytes:25<<20,max_text_chars:131_072,
            store_snippets:true,include_text:true,include_office:true,include_pdf:true,skip_system_folders:true }
    }
}

#[derive(Clone,Debug,Default)]
pub struct ContentScanEstimate {
    pub eligible_files:u64,pub already_indexed:u64,pub to_scan:u64,pub total_source_bytes:u64,pub estimated_index_bytes:u64,
}

#[derive(Clone,Debug,Default)]
pub struct ContentScanProgress {
    pub processed:u64,pub total:u64,pub indexed:u64,pub skipped:u64,pub failed:u64,pub current_file:String,
}

#[derive(Copy,Clone,Debug,Default,Eq,PartialEq)]
pub enum ContentMatchMode { #[default] Smart, AllWords, AnyWord, ExactPhrase }

#[derive(Clone,Debug)]
pub struct ContentSearchOptions { pub mode:ContentMatchMode,pub fuzzy_matching:bool }
impl Default for ContentSearchOptions {
    fn default()->Self { Self { mode:ContentMatchMode::Smart,fuzzy_matching:true } }
}

#[derive(Clone,Debug)]
pub struct ContentSearchHit {
    pub file_id:i64,pub volume:String,pub rel:String,pub name:String,pub full_path:PathBuf,
    pub volume_label:String,pub online:bool,pub bytes:i64,pub modified:i64,pub snippet:String,pub rank:f64,
}

#[derive(Clone,Debug,Default)]
pub struct ContentSearchResult {
    pub hits:Vec<ContentSearchHit>,pub summary:String,pub fuzzy_corrections:Vec<String>,pub used_relaxed_match:bool,
}

#[derive(Clone,Debug,Default)]
pub struct ContentIndexStats { pub indexed_files:u64,pub total_text_bytes:u64,pub database_bytes:u64 }

#[derive(Clone,Debug)]
pub struct ContentIndex { pub catalog:Catalog }

struct Record {
    volume:String,rel:String,bytes:i64,modified:i64,version:i32,status:i32,
    indexed_at:i64,snippet:Option<Vec<u8>>,text_bytes:i32,text_cap:i32,
}

struct Source {
    id:i64,volume:String,rel:String,mount:String,extension:String,bytes:i64,modified:i64,
    status:Option<i32>,snippet:bool,cap:usize,
}

impl Source {
    fn needs_scan(&self,options:&ContentScanOptions)->bool {
        match self.status {
            Some(1)=>self.snippet!=options.store_snippets || self.cap!=options.max_text_chars,
            Some(2)=>self.cap!=options.max_text_chars,
            _=>true,
        }
    }
}

impl ContentIndex {
    pub fn new(catalog:Catalog)->Self { Self {catalog} }
    pub fn database_path(&self)->PathBuf { self.catalog.data_dir.join("content.sqlite3") }

    pub fn open(&self)->EngineResult<Connection> {
        fs::create_dir_all(&self.catalog.data_dir)?;
        let db=Connection::open(self.database_path())?;
        configure(&db)?;
        db.execute_batch(SCHEMA)?;
        Ok(db)
    }

    fn attached(&self)->EngineResult<Connection> {
        self.catalog.open()?;
        let db=self.open()?;
        db.execute("ATTACH DATABASE ?1 AS source_catalog",[self.catalog.database_path().to_string_lossy().as_ref()])?;
        Ok(db)
    }

    fn visit_candidates(&self,db:&Connection,options:&ContentScanOptions,mut visit:impl FnMut(Source)->EngineResult<()>)->EngineResult<()> {
        if options.max_text_chars==0 || options.max_text_chars>MAX_FIELD/3 || options.max_file_bytes<0 {
            return Err(invalid("Invalid content scan limits").into());
        }
        let extensions=TEXT.split_ascii_whitespace().filter(|_|options.include_text)
            .chain(OFFICE.split_ascii_whitespace().filter(|_|options.include_office))
            .chain(["pdf"].into_iter().filter(|_|options.include_pdf)).collect::<Vec<_>>();
        let volumes=options.volume_ids.as_ref().map(serde_json::to_string).transpose()?;
        let mut statement=db.prepare("SELECT f.id,f.volume,f.rel,v.mount,f.extension,f.bytes,f.modified,c.status,
             c.brotli_text IS NOT NULL,coalesce(s.text_cap,0)
          FROM source_catalog.files f JOIN source_catalog.volumes v ON v.id=f.volume
          LEFT JOIN content_files c ON c.file_id=f.id AND (c.volume,c.rel,c.bytes,c.modified,c.extractor_ver)
             =(f.volume,f.rel,f.bytes,f.modified,?1)
          LEFT JOIN content_sizes s ON s.file_id=c.file_id
          WHERE v.online=1 AND f.removed IS NULL AND (f.attributes & ?2)=0 AND f.bytes BETWEEN 0 AND ?3
             AND f.extension IN (SELECT value FROM json_each(?4))
             AND (?5 IS NULL OR f.volume IN (SELECT value FROM json_each(?5)))")?;
        let mut rows=statement.query(params![EXTRACTOR,RECALL_RISK|16,options.max_file_bytes,serde_json::to_string(&extensions)?,volumes])?;
        while let Some(r)=rows.next()? {
            let source=Source{id:r.get(0)?,volume:r.get(1)?,rel:r.get(2)?,mount:r.get(3)?,extension:r.get(4)?,
                bytes:r.get(5)?,modified:r.get(6)?,status:r.get(7)?,snippet:r.get(8)?,cap:r.get(9)?};
            if !options.skip_system_folders || !noise_path(&source.rel) {visit(source)?;}
        }
        Ok(())
    }

    pub fn estimate_scan(&self,options:&ContentScanOptions)->EngineResult<ContentScanEstimate> {
        let db=self.attached()?;
        let mut estimate=ContentScanEstimate::default();
        let mut pending_text=0u64;
        self.visit_candidates(&db,options,|source| {
            estimate.eligible_files+=1;
            estimate.total_source_bytes=estimate.total_source_bytes.saturating_add(source.bytes as u64);
            if source.status==Some(1) {estimate.already_indexed+=1;}
            if source.needs_scan(options) {
                estimate.to_scan+=1;
                pending_text=pending_text.saturating_add((source.bytes as u64).min(options.max_text_chars as u64));
            }
            Ok(())
        })?;
        estimate.estimated_index_bytes=if options.store_snippets {pending_text.div_ceil(5)}
            else {pending_text.saturating_mul(2).div_ceil(25)};
        Ok(estimate)
    }
    pub fn scan(&self,options:&ContentScanOptions,cancel:Arc<AtomicBool>,mut report:impl FnMut(&ContentScanProgress))
        ->EngineResult<ContentScanProgress> {
        check_cancel(&cancel)?;
        let mut db=self.attached()?;
        let source_db=self.attached()?;
        source_db.execute_batch("BEGIN")?;
        let mut progress=ContentScanProgress::default();
        self.visit_candidates(&source_db,options,|_| {
            check_cancel(&cancel)?;
            progress.total+=1;
            Ok(())
        })?;
        report(&progress);
        let cpus=thread::available_parallelism().map_or(2,usize::from);
        let workers=match options.mode {
            ContentScanMode::Eco=>(cpus/4).clamp(1,2),
            ContentScanMode::Balanced=>(cpus/2).clamp(1,12),
            ContentScanMode::Turbo=>cpus.clamp(1,32),
        }.min(progress.total as usize);
        thread::scope(|scope| -> EngineResult<()> {
            let (work_tx,work_rx)=bounded(workers.max(1)*4);
            let (result_tx,result_rx)=bounded(workers.max(1)*2);
            let producer_cancel=&cancel;
            let producer=scope.spawn(move || self.visit_candidates(&source_db,options,|source| {
                check_cancel(producer_cancel)?;
                work_tx.send(source).map_err(|_|crate::util::interrupted())?;
                Ok(())
            }));
            for _ in 0..workers {
                let work_rx=work_rx.clone();
                let result_tx=result_tx.clone();
                let cancel=&cancel;
                scope.spawn(move || {
                    if options.mode==ContentScanMode::Eco { background_priority(); }
                    while let Ok(source)=work_rx.recv() {
                        if check_cancel(cancel).is_err() {break;}
                        if !source.needs_scan(options) {
                            if result_tx.send((source.rel,Ok(None))).is_err() {break;}
                            continue;
                        }
                        let body=content_extract::safe_source_path(&source.volume,&source.mount,&source.rel)
                            .map_err(Into::into).and_then(|path| {
                                let file=content_extract::open_safe(&path,source.bytes,source.modified)?;
                                content_extract::extract(file,&source.extension,options.max_text_chars,cancel)
                            });
                        let result=body.and_then(|body| {
                            let record=Record { volume:source.volume,rel:source.rel.clone(),bytes:source.bytes,
                                modified:source.modified,version:EXTRACTOR,status:if body.is_empty(){2}else{1},
                                indexed_at:now_ms(),snippet:if options.store_snippets && !body.is_empty() {
                                    Some(compress(body.as_bytes())?)
                                } else {None},text_bytes:body.len() as i32,text_cap:options.max_text_chars as i32 };
                            Ok(Some((source.id,record,body)))
                        });
                        if result_tx.send((source.rel,result)).is_err() {break;}
                    }
                });
            }
            drop(work_rx);
            drop(result_tx);
            while let Ok(first)=result_rx.recv() {
                let batch=std::iter::once(first).chain(result_rx.try_iter().take(31));
                let tx=db.transaction_with_behavior(TransactionBehavior::Immediate)?;
                for (rel,result) in batch {
                    check_cancel(&cancel)?;
                    progress.processed+=1;
                    progress.current_file=rel;
                    match result {
                        Ok(Some((id,record,body)))=>{
                            if lookup(&tx,&record)?!=Some(id) {progress.skipped+=1;continue;}
                            write(&tx,id,&record,&body)?;
                            if record.status==1 {progress.indexed+=1;} else {progress.skipped+=1;}
                        }
                        Ok(None)=>progress.skipped+=1,
                        Err(_)=>progress.failed+=1,
                    }
                }
                tx.commit()?;
                report(&progress);
            }
            check_cancel(&cancel)?;
            producer.join().map_err(|_|invalid("Content producer failed"))??;
            Ok(())
        })?;
        if progress.indexed>0 { optimize(&db)?; }
        Ok(progress)
    }

    pub fn optimize(&self)->EngineResult<()> { optimize(&self.open()?) }

    pub fn stats(&self)->EngineResult<ContentIndexStats> {
        let db=self.open()?;
        let (indexed_files,total_text_bytes)=db.query_row("SELECT count(*),coalesce(sum(s.text_bytes),0)
          FROM content_files c LEFT JOIN content_sizes s USING(file_id) WHERE c.status=1",[],|r|Ok((r.get(0)?,r.get(1)?)))?;
        let path=self.database_path();
        let wal=path.with_extension("sqlite3-wal");
        Ok(ContentIndexStats { indexed_files,total_text_bytes,database_bytes:fs::metadata(path)?.len()
            + fs::metadata(wal).map_or(0,|m|m.len()) })
    }

    pub fn search(&self,query:&str,volume:Option<&str>,extension:Option<&str>,limit:usize,
            options:&ContentSearchOptions)->EngineResult<ContentSearchResult> {
        if query.trim().is_empty() || limit==0 {return Ok(ContentSearchResult::default());}
        let invalid_query=|| ContentSearchResult {summary:"Enter a valid search with at least one positive term".into(),..Default::default()};
        let Some((root,explicit))=content_query::parse(query,options.mode) else {return Ok(invalid_query());};
        let mut positive=Vec::new();
        root.positive(false,&mut positive);
        if positive.is_empty() {return Ok(invalid_query());}
        let db=self.attached()?;
        db.execute_batch("BEGIN")?;
        let extension=extension.map(|s|normalize_key(s.trim_start_matches('.')));
        let concepts:Vec<_>=positive.iter().copied().filter(|term| term.prefix || term.phrase ||
            !STOP_WORDS.split_ascii_whitespace().any(|word|word.eq_ignore_ascii_case(&term.text))).collect();
        let smart=options.mode==ContentMatchMode::Smart && !explicit && positive.len()>=2 && !concepts.is_empty();
        let mut corrections=HashMap::new();
        let fetch=if smart && concepts.len()>=3 {limit.max(3)} else {limit};
        let mut hits=read_hits(&db,&root,&corrections,volume,extension.as_deref(),fetch)?;
        if options.fuzzy_matching {
            let mut seen=HashSet::new();
            let mut vocab=false;
            for word in positive.iter().filter(|t|!t.prefix && (!t.phrase || hits.is_empty()))
                .flat_map(|t|words(&t.text)).map(str::to_lowercase).filter(|word|seen.insert(word.clone())).take(32) {
                let len=word.chars().count();
                if !(3..=64).contains(&len) || word.chars().all(char::is_numeric) {continue;}
                if db.query_row("SELECT 1 FROM content_fts WHERE content_fts MATCH ?1 LIMIT 1",
                    [quote(&word,false)],|r|r.get::<_,i64>(0)).optional()?.is_some() {continue;}
                if !vocab {
                    db.execute_batch("CREATE VIRTUAL TABLE temp.content_vocab USING fts5vocab(main,content_fts,row)")?;
                    vocab=true;
                }
                let alternatives=spellings(&db,&word)?;
                if !alternatives.is_empty() {corrections.insert(word,alternatives);}
            }
            if !corrections.is_empty() {hits=read_hits(&db,&root,&corrections,volume,extension.as_deref(),fetch)?;}
        }
        let mut used_relaxed_match=false;
        if smart && (hits.is_empty() || (hits.len()<3 && concepts.len()>=3)) {
            let mut relaxed=concepts.iter().map(|&term|Node::Term(term.clone())).reduce(|a,b|a.join(b,true)).unwrap();
            let mut exclusions=Vec::new();
            root.exclusions(&mut exclusions);
            for node in exclusions {relaxed=relaxed.join(node.clone(),false);}
            let mut close=read_hits(&db,&relaxed,&corrections,volume,extension.as_deref(),limit.saturating_mul(3).max(150))?;
            if !close.is_empty() {
                let ids=serde_json::to_string(&close.iter().map(|(hit,_)|hit.file_id).collect::<Vec<_>>())?;
                let mut coverage=HashMap::<i64,usize>::new();
                for term in &concepts {
                    for id in rows(&db,"SELECT rowid FROM content_fts WHERE content_fts MATCH ?1
                      AND rowid IN (SELECT value FROM json_each(?2))",params![term.render(&corrections,false),ids],|r|r.get(0))? {
                        *coverage.entry(id).or_default()+=1;
                    }
                }
                close.sort_by(|(a,_),(b,_)|coverage.get(&b.file_id).cmp(&coverage.get(&a.file_id))
                    .then_with(||a.rank.total_cmp(&b.rank)).then(a.file_id.cmp(&b.file_id)));
                hits=close;
                used_relaxed_match=true;
            }
        }
        let snippet_terms:Vec<_>=positive.into_iter().flat_map(|term|words(&term.text))
            .flat_map(|word|std::iter::once(word.to_lowercase()).chain(
                corrections.get(&word.to_lowercase()).into_iter().flatten().cloned())).collect();
        let hits=hits.into_iter().take(limit).map(|(mut hit,body)|{
            hit.snippet=body.as_deref().and_then(|compressed|decompress(compressed).ok())
                .map(|body|snippet(&body,&snippet_terms).split_whitespace().collect::<Vec<_>>().join(" "))
                .unwrap_or_default();
            hit
        }).collect();
        let mut fuzzy_corrections:Vec<_>=corrections.into_iter().map(|(word,values)|format!("{word} → {}*",values[0])).collect();
        fuzzy_corrections.sort_unstable();
        Ok(ContentSearchResult {hits,fuzzy_corrections,used_relaxed_match,
            summary:if used_relaxed_match {"Close match (matched best keywords)".into()} else {String::new()} })
    }

    pub fn export_content(&self,destination:&Path,volume:Option<&str>)->EngineResult<()> {
        self.export_with_cancel(destination,volume,&AtomicBool::new(false),&HashMap::new())
    }

    pub fn export_content_with_cancel(&self,destination:&Path,volume:Option<&str>,cancel:&AtomicBool)->EngineResult<()> {
        self.export_with_cancel(destination,volume,cancel,&HashMap::new())
    }

    pub(crate) fn export_with_cancel(&self,destination:&Path,volume:Option<&str>,cancel:&AtomicBool,
            aliases:&HashMap<String,String>)->EngineResult<()> {
        let db=self.open()?;
        db.execute_batch("CREATE VIRTUAL TABLE temp.content_export_terms USING fts5vocab(main,content_fts,instance); BEGIN;")?;
        let mut statement=db.prepare("SELECT c.file_id,volume,rel,bytes,modified,extractor_ver,status,indexed_at,
          brotli_text,text_bytes,text_cap FROM content_files c JOIN content_sizes s ON s.file_id=c.file_id
          WHERE (?1 IS NULL OR volume=?1) ORDER BY c.file_id")?;
        let mut records=statement.query([volume])?;
        atomic_write(destination, |file| -> EngineResult<()> {
            crate::sync::write_brotli(file, |mut output| {
            output.write_all(b"FICONTENT1")?;
            while let Some(row)=records.next()? {
                check_cancel(cancel)?;
                let id:i64=row.get(0)?;
                let record=Record {volume:row.get(1)?,rel:row.get(2)?,bytes:row.get(3)?,modified:row.get(4)?,
                    version:row.get(5)?,status:row.get(6)?,indexed_at:row.get(7)?,snippet:row.get(8)?,
                    text_bytes:row.get(9)?,text_cap:row.get(10)?};
                let terms=if record.status==1 && record.snippet.is_none() {
                    let terms:Vec<String>=rows(&db,"SELECT term FROM content_export_terms WHERE doc=?1 ORDER BY offset",[id],|r|r.get(0))?;
                    Some(terms.join(" "))
                } else {None};
                // Archives sent through cloud sync are also usable with a remote offline inventory.
                for volume in std::iter::once(&record.volume).chain(aliases.get(&record.volume)) {
                    output.write_all(&[1])?;
                    write_field(&mut output,volume.as_bytes())?;
                    write_field(&mut output,record.rel.as_bytes())?;
                    output.write_all(&record.bytes.to_le_bytes())?;
                    output.write_all(&record.modified.to_le_bytes())?;
                    output.write_all(&record.version.to_le_bytes())?;
                    output.write_all(&record.status.to_le_bytes())?;
                    output.write_all(&record.indexed_at.to_le_bytes())?;
                    output.write_all(&record.text_bytes.to_le_bytes())?;
                    output.write_all(&record.text_cap.to_le_bytes())?;
                    output.write_all(&[u8::from(record.snippet.is_some())])?;
                    if let Some(snippet)=&record.snippet {write_field(&mut output,snippet)?;}
                    else if let Some(terms)=&terms {write_field(&mut output,terms.as_bytes())?;}
                }
            }
            check_cancel(cancel)?;
            output.write_all(&[0])?;
            Ok(())
            })
        })
    }

    pub fn import_content(&self,source:&Path)->EngineResult<usize> {
        self.import_with_cancel(source,&AtomicBool::new(false))
    }

    pub fn import_content_with_cancel(&self,source:&Path,cancel:&AtomicBool)->EngineResult<usize> {
        self.import_with_cancel(source,cancel)
    }

    pub(crate) fn import_with_cancel(&self,source:&Path,cancel:&AtomicBool)->EngineResult<usize> {
        let mut reader=brotli::Decompressor::new(File::open(source)?,65536);
        if &read_fixed::<10>(&mut reader)?!=b"FICONTENT1" {return Err(invalid("Unrecognized content archive").into());}
        let mut db=self.attached()?;
        let tx=db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let mut imported=0;
        while read_bool(&mut reader)? {
            check_cancel(cancel)?;
            let record=Record {
                volume:String::from_utf8(read_field(&mut reader)?)?, rel:String::from_utf8(read_field(&mut reader)?)?,
                bytes:i64::from_le_bytes(read_fixed(&mut reader)?),modified:i64::from_le_bytes(read_fixed(&mut reader)?),
                version:i32::from_le_bytes(read_fixed(&mut reader)?),status:i32::from_le_bytes(read_fixed(&mut reader)?),
                indexed_at:i64::from_le_bytes(read_fixed(&mut reader)?),text_bytes:i32::from_le_bytes(read_fixed(&mut reader)?),
                text_cap:i32::from_le_bytes(read_fixed(&mut reader)?),
                snippet:if read_bool(&mut reader)? {Some(read_field(&mut reader)?)}else{None},
            };
            if !safe_relative(&record.rel) || record.bytes<0 || !(0..=MAX_FIELD as i32).contains(&record.text_bytes)
                || record.text_cap<=0 || !matches!(record.status,1|2) || (record.status==2 && record.snippet.is_some()) {
                return Err(invalid("Invalid content archive record").into());
            }
            let body=if let Some(ref snippet)=record.snippet {decompress(snippet)?}
                else if record.status==1 {String::from_utf8(read_field(&mut reader)?)?} else {String::new()};
            if let Some(id)=lookup(&tx,&record)? {write(&tx,id,&record,&body)?;imported+=1;}
        }
        if reader.read(&mut [0])?!=0 {return Err(invalid("Unexpected content archive data").into());}
        check_cancel(cancel)?;
        tx.commit()?;
        if imported>0 {optimize(&db)?;}
        Ok(imported)
    }
}

fn lookup(db:&Connection,r:&Record)->rusqlite::Result<Option<i64>> {
    db.query_row("SELECT f.id FROM source_catalog.files f
        WHERE (f.volume,f.rel,f.bytes,f.modified)=(?1,?2,?3,?4) AND f.removed IS NULL AND (f.attributes & 16)=0
        AND NOT EXISTS(SELECT 1 FROM content_files c WHERE c.file_id=f.id AND
         (c.volume,c.rel,c.bytes,c.modified)=(f.volume,f.rel,f.bytes,f.modified)
         AND (c.extractor_ver,c.indexed_at)>=(?5,?6))",
        params![r.volume,r.rel,r.bytes,r.modified,r.version,r.indexed_at],|r|r.get(0)).optional()
}
fn write(db:&Connection,id:i64,r:&Record,body:&str)->rusqlite::Result<()> {
    db.execute("DELETE FROM content_fts WHERE rowid=?1",[id])?;
    if r.status==1 {db.execute("INSERT INTO content_fts(rowid,body) VALUES(?1,?2)",params![id,body])?;}
    db.execute("INSERT OR REPLACE INTO content_files VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)",
        params![id,r.volume,r.rel,r.bytes,r.modified,r.version,r.status,r.indexed_at,r.snippet])?;
    db.execute("INSERT OR REPLACE INTO content_sizes VALUES(?1,?2,?3)",params![id,r.text_bytes,r.text_cap])?;
    Ok(())
}
fn optimize(db:&Connection)->EngineResult<()> {
    db.execute_batch("INSERT INTO main.content_fts(content_fts) VALUES('optimize');
        PRAGMA main.incremental_vacuum; PRAGMA main.optimize;")?;
    Ok(())
}

fn read_hits(db:&Connection,node:&Node,corrections:&HashMap<String,Vec<String>>,volume:Option<&str>,
    extension:Option<&str>,limit:usize)->rusqlite::Result<Vec<(ContentSearchHit,Option<Vec<u8>>)>> {
    let (expression,complement)=node.compile(corrections,false);
    let predicate=if complement {"content_fts.rowid NOT IN (SELECT rowid FROM content_fts WHERE content_fts MATCH $query)"}
        else {"content_fts MATCH $query"};
    rows(db,&format!("SELECT f.id,f.volume,f.rel,f.name,v.mount,v.label,v.online,f.bytes,f.modified,c.brotli_text,
          bm25(content_fts) AS rank FROM content_fts JOIN content_files c ON c.file_id=content_fts.rowid
        JOIN source_catalog.files f ON f.id=c.file_id AND (f.volume,f.rel,f.bytes,f.modified)=(c.volume,c.rel,c.bytes,c.modified)
        JOIN source_catalog.volumes v ON v.id=f.volume WHERE {predicate} AND f.removed IS NULL AND (f.attributes&16)=0
        AND ($volume IS NULL OR f.volume=$volume) AND ($ext IS NULL OR f.extension=$ext)
        ORDER BY rank,f.id LIMIT $limit"),
        named_params!{"$query":expression,"$volume":volume,"$ext":extension,"$limit":sql_limit(limit)},|r|{
            let rel:String=r.get(2)?;
            let mount:String=r.get(4)?;
            Ok((ContentSearchHit{file_id:r.get(0)?,volume:r.get(1)?,name:r.get(3)?,full_path:local_path(&mount,&rel),
                rel,volume_label:r.get(5)?,online:r.get(6)?,bytes:r.get(7)?,modified:r.get(8)?,snippet:String::new(),
                rank:r.get(10)?},r.get(9)?))
        })
}
fn spellings(db:&Connection,word:&str)->rusqlite::Result<Vec<String>> {
    let len=word.chars().count();
    let tolerance=match len {0..=5=>1,6..=10=>2,_=>3};
    let prefix=|word:&str,count:usize|word.chars().take(count).collect::<String>();
    let chars:Vec<_>=word.chars().collect();
    let swapped=format!("{}{}",chars[1],chars[0]);
    let repaired=format!("{swapped}{}",chars[2..].iter().collect::<String>());
    let stem=["ing","ed"].iter().find_map(|suffix|word.strip_suffix(suffix).filter(|s|s.chars().count()>=3))
        .unwrap_or(word);
    let mut best=Vec::new();
    let mut seen=HashSet::new();
    let mut probe=|start:String, best:&mut Vec<_>|->rusqlite::Result<()> {
        let upper=format!("{start}\u{10FFFF}");
        let mut statement=db.prepare_cached("SELECT term,doc FROM content_vocab WHERE term>=?1 AND term<?2 LIMIT 3000")?;
        let mut rows=statement.query(params![start,upper])?;
        while let Some(row)=rows.next()? {
            let candidate:String=row.get(0)?;
            let docs:i64=row.get(1)?;
            if candidate.chars().count().abs_diff(len)>tolerance+3 || !seen.insert(candidate.clone()) {continue;}
            let score=|typed:&str| {
                let mut distance=content_query::distance(typed,&candidate);
                if distance>1 && typed.ends_with('e') && !candidate.ends_with('e') {
                    distance=distance.min(content_query::distance(typed,&format!("{candidate}e")));
                }
                distance
            };
            let mut distance=score(word).min(score(stem));
            if distance>tolerance && len>=6 && candidate.starts_with(&swapped) {distance=score(&repaired);}
            if distance<=tolerance {
                let common=word.chars().zip(candidate.chars()).take_while(|(a,b)|a==b).count();
                best.push((distance,std::cmp::Reverse(common),std::cmp::Reverse(docs),candidate));
                best.sort_unstable();
                best.truncate(4);
            }
        }
        Ok(())
    };
    probe(prefix(word,2),&mut best)?;
    probe(swapped.clone(),&mut best)?;
    if len<=5 || best.len()<3 {probe(prefix(word,1),&mut best)?;}
    if best.is_empty() {
        probe(chars[1].to_string(),&mut best)?;
        if best.is_empty() {
            for initial in 'a'..='z' {probe(format!("{initial}{}{}",chars[1],chars[2]),&mut best)?;}
        }
    }
    Ok(best.into_iter().map(|(_,_,_,term)|term).collect())
}

const STOP_WORDS:&str="a an the and or of to in on for with about from by at as is are was were be been that this these those it its my your our their file files document documents looking find something where what when how into onto over under between through during before after above below up down out off all any both each few more most other some such only own same so than too very can will just should now";
fn snippet(body:&str,terms:&[String])->String {
    const WIDTH:usize=240;
    let body_chars:Vec<_>=body.char_indices().collect();
    if body_chars.len()<=WIDTH {return body.to_owned();}
    let mut hits=Vec::new();
    for (i,word) in words(body).enumerate() {
        let folded=word.to_lowercase();
        for (term_id,term) in terms.iter().enumerate() {
            if folded.starts_with(term) {
                let offset=word.as_ptr() as usize-body.as_ptr() as usize;
                let char_index=body_chars.partition_point(|&(byte,_)|byte<offset);
                hits.push((char_index,term_id));
            }
        }
        if i>=20_000 {break;}
    }
    let mut start=0;
    let mut best=0;
    let mut counts=HashMap::<usize,usize>::new();
    let (mut left,mut distinct)=(0,0);
    for (right,&(position,term)) in hits.iter().enumerate() {
        let count=counts.entry(term).or_default();
        if *count==0 {distinct+=1;}
        *count+=1;
        while position-hits[left].0>=WIDTH-30 {
            let count=counts.get_mut(&hits[left].1).unwrap();
            *count-=1;
            if *count==0 {distinct-=1;}
            left+=1;
        }
        if distinct>best {
            best=distinct;
            start=((hits[left].0+hits[right].0)/2).saturating_sub(WIDTH/2);
        }
    }
    start=start.min(body_chars.len()-WIDTH);
    let end=start+WIDTH;
    let end_byte=body_chars.get(end).map_or(body.len(),|&(i,_)|i);
    format!("{}{}{}",if start>0 {"…"} else {""},body[body_chars[start].0..end_byte].trim(),
        if end<body_chars.len() {"…"} else {""})
}

fn compress(input:&[u8])->io::Result<Vec<u8>> {
    let mut encoder=brotli::CompressorWriter::new(Vec::new(),4096,4,21);
    encoder.write_all(input)?;
    Ok(encoder.into_inner())
}
fn decompress(input:&[u8])->io::Result<String> {
    let mut text=Vec::new();
    brotli::Decompressor::new(input,4096).take(MAX_FIELD as u64+1).read_to_end(&mut text)?;
    if text.len()>MAX_FIELD {return Err(invalid("Content text exceeds limit"));}
    String::from_utf8(text).map_err(|error|invalid(error.to_string()))
}
fn write_field(output:&mut impl Write,data:&[u8])->io::Result<()> {
    if data.len()>MAX_FIELD {return Err(invalid("Content archive field exceeds limit"));}
    output.write_all(&(data.len() as i32).to_le_bytes())?;
    output.write_all(data)
}
fn read_field(input:&mut impl Read)->io::Result<Vec<u8>> {
    let len=u32::from_le_bytes(read_fixed(input)?) as usize;
    if len>MAX_FIELD {return Err(invalid("Invalid content archive field length"));}
    let mut field=vec![0;len];
    input.read_exact(&mut field)?;
    Ok(field)
}
fn read_fixed<const N:usize>(input:&mut impl Read)->io::Result<[u8;N]> {
    let mut result=[0;N];
    input.read_exact(&mut result)?;
    Ok(result)
}
fn read_bool(input:&mut impl Read)->io::Result<bool> {
    match read_fixed::<1>(input)?[0] {0=>Ok(false),1=>Ok(true),_=>Err(invalid("Invalid content archive marker"))}
}
pub(crate) fn safe_relative(rel:&str)->bool {
    !rel.is_empty() && rel.encode_utf16().count()<=32767 && rel.split('\\').all(|part|!
        (part.is_empty() || part=="." || part==".." || part.ends_with(['.',' ']) ||
         part.chars().any(|c|c<' ' || ":/<>\"|?*".contains(c))))
}
fn noise_path(rel:&str)->bool {
    let key=rel.to_ascii_lowercase();
    ["windows\\","program files\\","program files (x86)\\","programdata\\","$recycle.bin\\","system volume information\\"]
        .iter().any(|prefix|key.starts_with(prefix))
        || ["\\node_modules\\","\\.git\\","\\appdata\\local\\temp\\","\\appdata\\local\\packages\\","\\obj\\","\\bin\\"]
            .iter().any(|part|key.contains(part))
}

fn background_priority() {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::System::Threading::{GetCurrentThread,SetThreadPriority,THREAD_MODE_BACKGROUND_BEGIN};
        SetThreadPriority(GetCurrentThread(),THREAD_MODE_BACKGROUND_BEGIN as i32);
    }
}







