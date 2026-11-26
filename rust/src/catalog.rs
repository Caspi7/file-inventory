use std::{collections::HashSet, fs, io, path::PathBuf, time::Duration};
use chrono::{Local, NaiveDate, TimeZone};
use rusqlite::{functions::FunctionFlags, named_params, params, Connection, OptionalExtension, Params, Result, Row, TransactionBehavior};
use serde::{Deserialize, Serialize};
use crate::util::{catalog_key, invalid, local_path, normalize_key, now_ms, parent, read_json, tokenize_prefixes, write_json};

pub const SCHEMA: &str = include_str!("../../Catalog.sql");
pub const SEARCH: &str = include_str!("../../Search.sql");
pub(crate) const PUBLISH: &str = include_str!("../../Publish.sql");
pub(crate) const ENTRY_COLUMNS: &str = "f.id,f.volume,f.rel,f.name,v.mount,v.label,v.online,f.stale,f.bytes,f.modified,f.created,f.seen,f.attributes,f.removed,f.extension,f.file_key";

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Volume {
    pub id: String, pub mount: String, pub label: String, pub online: bool,
    pub enabled: bool, pub hours: f64, pub attempted: Option<i64>, pub succeeded: Option<i64>,
    pub outcome: String, pub issues: String, pub host: String, pub removable: bool,
    pub note: String, pub freshness_days: i32,
}

#[derive(Clone, Copy, Debug)]
pub enum FileSortColumn { Name, Extension, Path, Volume, Status, Size, Modified }

#[derive(Clone, Copy, Debug)]
pub struct FileSort {
    pub column: FileSortColumn,
    pub ascending: bool,
}

impl FileSort {
    fn order_by(self) -> String {
        let value = match self.column {
            FileSortColumn::Name => "name_key",
            FileSortColumn::Extension => "extension",
            FileSortColumn::Path => "rtrim(replace(mount, '/', char(92)), char(92)) || char(92) || rel COLLATE NOCASE",
            FileSortColumn::Volume => "coalesce(nullif(label, ''), mount) COLLATE NOCASE",
            FileSortColumn::Status => "CASE WHEN removed IS NOT NULL THEN 'Removed'
                WHEN online=0 AND stale<>0 THEN 'Offline · stale'
                WHEN online=0 THEN 'Offline' WHEN stale<>0 THEN 'Stale' ELSE 'Online' END",
            FileSortColumn::Size => "bytes",
            FileSortColumn::Modified => "modified",
        };
        let direction = if self.ascending { "ASC" } else { "DESC" };
        format!("{value} {direction}, name_key, volume, rel")
    }
}

#[derive(Clone, Debug)]
pub struct FileEntry {
    pub id: i64, pub volume: String, pub rel: String, pub name: String, pub mount: String,
    pub label: String, pub online: bool, pub stale: bool, pub bytes: i64, pub modified: i64,
    pub created: i64, pub seen: i64, pub attributes: u32, pub removed: Option<i64>,
    pub extension: String, pub file_key: Option<String>,
}

impl FileEntry {
    pub fn full_path(&self) -> PathBuf { local_path(&self.mount, &self.rel) }
    pub fn is_directory(&self) -> bool { self.attributes & 16 != 0 }
    pub fn status(&self) -> &'static str {
        match (self.removed.is_some(), self.online, self.stale) {
            (true, _, _) => "Removed",
            (_, true, false) => "Online",
            (_, true, true) => "Stale",
            (_, false, false) => "Offline",
            (_, false, true) => "Offline · stale",
        }
    }
}

#[derive(Clone, Debug)]
pub struct FolderNode { pub rel: String, pub name: String, pub has_children: bool }

#[derive(Clone, Debug)]
pub struct ScanRecord {
    pub id: i64, pub volume: String, pub started: i64, pub finished: Option<i64>, pub outcome: String,
    pub items: i64, pub issues: String, pub identity_ms: i64, pub added_count: i64,
    pub modified_count: i64, pub removed_count: i64, pub moved_count: i64, pub bytes_delta: i64,
}

#[derive(Clone, Debug)]
pub struct ScanChange {
    pub scan_id: i64, pub volume: String, pub rel: String, pub kind: i32,
    pub old_bytes: Option<i64>, pub new_bytes: Option<i64>,
}

#[derive(Clone, Debug, Default)]
pub struct FileAnnotation { pub tags: String, pub note: String, pub updated: i64 }

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct SearchFilter {
    #[serde(skip)]
    pub query: String,
    pub volume: Option<String>, pub extension: Option<String>, pub min: Option<i64>, pub max: Option<i64>,
    pub from: Option<i64>, pub through: Option<i64>, pub online: Option<bool>,
    pub drives: Option<Vec<String>>, pub include_removed: bool,
}
impl Default for SearchFilter {
    fn default() -> Self {
        Self { query: String::new(), volume: None, extension: None, min: None, max: None,
            from: None, through: None, online: None, drives: None, include_removed: true }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct SavedSearch { pub name: String, pub query: String, pub filters: SearchFilter }

impl SavedSearch {
    pub fn search_filter(&self) -> SearchFilter {
        SearchFilter { query: self.query.clone(), ..self.filters.clone() }
    }
}

#[derive(Clone, Debug)]
pub struct StorageMapItem {
    pub rel: String, pub name: String, pub extension: String, pub bytes: i64,
    pub file_count: i64, pub is_directory: bool,
}

#[derive(Clone, Debug)]
pub struct Catalog { pub data_dir: PathBuf }

impl Catalog {
    pub fn new(data_dir: impl Into<PathBuf>) -> Self { Self { data_dir: data_dir.into() } }
    pub fn database_path(&self) -> PathBuf { self.data_dir.join("catalog.sqlite3") }

    pub fn open(&self) -> Result<Connection> {
        fs::create_dir_all(&self.data_dir).map_err(sql_error)?;
        let mut db = Connection::open(self.database_path())?;
        configure(&db)?;
        db.execute_batch(SCHEMA)?;
        db.execute_batch("PRAGMA journal_mode=WAL;")?;
        register_prefixes(&db, Vec::new())?;
        db.create_scalar_function("folder_parent", 1, FunctionFlags::SQLITE_DETERMINISTIC, |ctx| {
            let rel = ctx.get::<String>(0)?;
            Ok(parent(&rel).to_owned())
        })?;
        migrate(&mut db)?;
        Ok(db)
    }

    pub fn discover_volumes(&self) -> Result<Vec<Volume>> {
        self.update_mounted_volumes(&mounted_volumes())?;
        self.volumes()
    }

    pub fn update_mounted_volumes(&self, volumes: &[Volume]) -> Result<()> {
        let mut db = self.open()?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        tx.execute("UPDATE volumes SET online=0 WHERE online<>0", [])?;
        for volume in volumes { upsert_mounted(&tx, volume)?; }
        tx.commit()
    }

    pub fn volumes(&self) -> Result<Vec<Volume>> {
        rows(&self.open()?, "SELECT id,mount,label,online,enabled,hours,attempted,succeeded,outcome,issues,host,removable,note,freshness_days FROM volumes ORDER BY online DESC,mount,label", [], |r| Ok(Volume {
            id:r.get(0)?, mount:r.get(1)?, label:r.get(2)?, online:r.get(3)?, enabled:r.get(4)?, hours:r.get(5)?,
            attempted:r.get(6)?, succeeded:r.get(7)?, outcome:r.get(8)?, issues:r.get(9)?, host:r.get(10)?,
            removable:r.get(11)?, note:r.get(12)?, freshness_days:r.get(13)?,
        }))
    }

    pub fn update_volume_schedule(&self, id: &str, enabled: bool, hours: f64) -> Result<()> {
        if !hours.is_finite() || hours < 0.0 { return Err(sql_error(invalid("Invalid schedule interval"))); }
        self.open()?.execute("UPDATE volumes SET enabled=?2,hours=?3 WHERE id=?1", params![id,enabled,hours])?;
        Ok(())
    }

    pub fn update_volume_meta(&self, id: &str, note: &str, freshness_days: i32) -> Result<()> {
        if freshness_days < 0 { return Err(sql_error(invalid("Freshness days must be nonnegative"))); }
        self.open()?.execute("UPDATE volumes SET note=?2,freshness_days=?3 WHERE id=?1", params![id,note,freshness_days])?;
        Ok(())
    }

    pub fn delete_volume(&self, id: &str) -> Result<()> {
        let mut db = self.open()?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        for table in ["scan_coverage","moves"] {
            tx.execute(&format!("DELETE FROM {table} WHERE scan_id IN (SELECT id FROM scans WHERE volume=?1)"), [id])?;
        }
        for table in ["files","folder_stats","file_annotations","file_tags","scan_changes","scans"] {
            tx.execute(&format!("DELETE FROM {table} WHERE volume=?1"), [id])?;
        }
        tx.execute("DELETE FROM volumes WHERE id=?1", [id])?;
        tx.commit()?;
        let _ = db.execute_batch("PRAGMA incremental_vacuum(2000);");
        let content_db_path = self.data_dir.join("content.sqlite3");
        if content_db_path.exists() {
            if let Ok(mut cdb) = Connection::open(&content_db_path) {
                let _ = configure(&cdb);
                if let Ok(ctx) = cdb.transaction_with_behavior(TransactionBehavior::Immediate) {
                    let _ = ctx.execute("DELETE FROM content_fts WHERE rowid IN (SELECT file_id FROM content_files WHERE volume=?1 AND status=1)", [id]);
                    let _ = ctx.execute("DELETE FROM content_meta_fts WHERE rowid IN (SELECT file_id FROM content_files WHERE volume=?1)", [id]);
                    let _ = crate::semantic::delete_vectors_for_volume(&ctx, id);
                    let _ = ctx.execute("DELETE FROM content_sizes WHERE file_id IN (SELECT file_id FROM content_files WHERE volume=?1)", [id]);
                    let _ = ctx.execute("DELETE FROM content_files WHERE volume=?1", [id]);
                    let _ = ctx.commit();
                }
                let _ = cdb.execute_batch("PRAGMA incremental_vacuum(2000);");
            }
        }
        Ok(())
    }

    pub fn browse_folder(&self, volume_id: &str, parent_rel: &str, show_removed: bool, limit: usize) -> Result<Vec<FileEntry>> {
        self.browse_folder_page(volume_id, parent_rel, show_removed, 0, limit)
    }

    pub fn browse_folder_page(&self, volume_id: &str, parent_rel: &str, show_removed: bool, offset: usize, limit: usize) -> Result<Vec<FileEntry>> {
        self.browse_folder_page_sorted(volume_id, parent_rel, show_removed, offset, limit, None)
    }

    pub fn browse_folder_page_sorted(&self, volume_id: &str, parent_rel: &str, show_removed: bool, offset: usize, limit: usize, sort: Option<FileSort>) -> Result<Vec<FileEntry>> {
        let order = sort.map_or_else(|| "(f.attributes & 16) DESC,f.name_key,f.rel".into(), FileSort::order_by);
        rows(&self.open()?, &format!("SELECT {ENTRY_COLUMNS} FROM files f JOIN volumes v ON v.id=f.volume
          WHERE f.volume=?1 AND f.parent_key=?2 AND (?3 OR f.removed IS NULL)
          ORDER BY {order} LIMIT ?4 OFFSET ?5"), params![volume_id,catalog_key(parent_rel),show_removed,sql_limit(limit),sql_limit(offset)], file_entry)
    }

    pub fn child_folders(&self, volume_id: &str, parent_rel: &str) -> Result<Vec<FolderNode>> {
        rows(&self.open()?, "SELECT f.rel,f.name,EXISTS(SELECT 1 FROM files c WHERE c.volume=f.volume
          AND c.parent_key=path_key(f.rel) AND (c.attributes & 16)<>0 AND c.removed IS NULL)
          FROM files f WHERE f.volume=?1 AND f.parent_key=?2 AND (f.attributes & 16)<>0 AND f.removed IS NULL
          ORDER BY f.name_key,f.rel", params![volume_id,catalog_key(parent_rel)], |r| Ok(FolderNode {
            rel:r.get(0)?, name:r.get(1)?, has_children:r.get(2)?,
        }))
    }

    pub fn search(&self, filter: &SearchFilter, limit: usize) -> Result<Vec<FileEntry>> {
        self.search_page(filter, 0, limit)
    }

    pub fn search_page(&self, filter: &SearchFilter, offset: usize, limit: usize) -> Result<Vec<FileEntry>> {
        self.search_page_sorted(filter, offset, limit, None)
    }

    pub fn search_page_sorted(&self, filter: &SearchFilter, offset: usize, limit: usize, sort: Option<FileSort>) -> Result<Vec<FileEntry>> {
        let order = sort.map(FileSort::order_by);
        let parsed = ParsedSearch::parse(filter).map_err(sql_error)?;
        let db = self.open()?;
        let mut filter = parsed.filter;
        if let Some(volume) = &filter.volume {
            let mut statement = db.prepare("SELECT id FROM volumes WHERE id=?1 COLLATE NOCASE OR rtrim(mount, char(92) || '/')=rtrim(?1, char(92) || '/') COLLATE NOCASE OR label=?1 COLLATE NOCASE ORDER BY id=?1 DESC LIMIT 2")?;
            let ids = statement.query_map([volume], |row| row.get::<_, String>(0))?
                .collect::<Result<Vec<_>>>()?;
            if let Some(id) = ids.first().filter(|id| ids.len() == 1 || *id == volume) {
                filter.volume = Some(id.clone());
            }
        }
        let short: Vec<_> = parsed.terms.iter().filter(|term| term.chars().count() < 3).cloned().collect();
        let long: Vec<_> = parsed.terms.iter().filter(|term| term.chars().count() >= 3)
            .map(|term| format!("\"{}\"", term.replace('"', "\"\""))).collect();
        let fts = (!long.is_empty()).then(|| format!("({})", long.join(" AND ")));
        register_prefixes(&db, short.clone())?;
        let excludes = parsed.exclusions;
        db.create_scalar_function("excluded_name", 1, FunctionFlags::SQLITE_DETERMINISTIC, move |ctx| {
            let name = ctx.get::<String>(0)?;
            Ok(excludes.iter().any(|term| if term.chars().count() < 3 {
                tokenize_prefixes(&name).contains(term)
            } else { name.contains(term) }))
        })?;
        let extra = " AND ($drives IS NULL OR f.volume IN (SELECT value FROM json_each($drives)))
            AND ($include OR f.removed IS NULL)
            AND ($status IS NULL OR CASE WHEN f.removed IS NOT NULL THEN 'removed'
                 WHEN v.online=0 THEN 'offline' WHEN f.stale<>0 THEN 'stale' ELSE 'online' END=$status
                 OR ($status='stale' AND f.stale<>0 AND f.removed IS NULL))
            AND ($tag IS NULL OR EXISTS(SELECT 1 FROM file_tags t WHERE t.tag=$tag AND t.volume=f.volume AND t.rel=f.rel))
            AND NOT excluded_name(f.name_key)";
        let ranked = if parsed.terms.is_empty() {
            let inner_order = order.as_deref().unwrap_or("(f.attributes & 16) DESC,f.name_key,f.volume,f.rel");
            format!("SELECT f.id FROM files f JOIN volumes v ON v.id=f.volume
               WHERE ($volume IS NULL OR f.volume=$volume) {extra}
               AND ($extension IS NULL OR f.extension=$extension) AND ($min IS NULL OR f.bytes >= $min)
               AND ($max IS NULL OR f.bytes <= $max) AND ($modifiedFrom IS NULL OR f.modified >= $modifiedFrom)
               AND ($modifiedThrough IS NULL OR f.modified < $modifiedThrough)
               AND ($online IS NULL OR v.online=$online) AND $match IS NULL AND $firstShort IS NULL AND $query=''
               ORDER BY {inner_order} LIMIT $limit OFFSET $offset")
        } else {
            let inner_order = order.as_deref().unwrap_or("rank,length(name),name_key,volume,rel");
            SEARCH.replace("WHERE ($volume IS NULL OR f.volume=$volume)",
                &format!("WHERE ($volume IS NULL OR f.volume=$volume) {extra}"))
                .replace("ELSE 3 END AS rank", "WHEN NOT matches_name_only(parent_key,$query) THEN 3 ELSE 4 END AS rank")
                .replace("matches_name_prefixes(name_key || ' ' || parent_key)",
                    if parsed.terms.len() == 1 { "matches_name_prefixes(name_key)" }
                    else { "matches_name_prefixes(name_key || ' ' || parent_key)" })
                .replace("SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,rank,0,removed", "SELECT id")
                .replace("ORDER BY rank,length(name),name_key,volume,rel LIMIT 50;",
                    &format!("ORDER BY {inner_order} LIMIT $limit OFFSET $offset"))
        };
        let outer_order = order.map_or_else(String::new, |order| format!(" ORDER BY {order}"));
        let sql = format!("WITH selected AS MATERIALIZED ({ranked}) SELECT {ENTRY_COLUMNS}
           FROM selected CROSS JOIN files f ON f.id=selected.id JOIN volumes v ON v.id=f.volume{outer_order}");
        let drives = filter.drives.as_ref().map(|drives| serde_json::to_string(drives).unwrap());
        rows(&db,&sql,named_params!{
            "$match":fts, "$firstShort":short.first(), "$query":parsed.terms.join(" "),
            "$volume":filter.volume, "$extension":filter.extension.map(|s|normalize_key(s.trim_start_matches('.'))),
            "$min":filter.min, "$max":filter.max, "$modifiedFrom":filter.from, "$modifiedThrough":filter.through,
            "$online":filter.online, "$drives":drives, "$include":filter.include_removed,
            "$tag":parsed.tag, "$status":parsed.status, "$limit":sql_limit(limit), "$offset":sql_limit(offset),
        }, file_entry)
    }

    pub fn folder_stats_children(&self, volume_id: &str, parent_rel: &str) -> Result<Vec<StorageMapItem>> {
        rows(&self.open()?, "SELECT f.rel,f.name,f.extension,
          CASE WHEN (f.attributes & 16)<>0 THEN coalesce(s.subtree_bytes,0) ELSE f.bytes END AS size,
          CASE WHEN (f.attributes & 16)<>0 THEN coalesce(s.subtree_files,0) ELSE 1 END,(f.attributes & 16)<>0
          FROM files f LEFT JOIN folder_stats s ON s.volume=f.volume AND s.rel=f.rel
          WHERE f.volume=?1 AND f.parent_key=?2 AND f.removed IS NULL ORDER BY size DESC,f.name_key,f.rel LIMIT 250",
          params![volume_id,catalog_key(parent_rel)], |r| Ok(StorageMapItem { rel:r.get(0)?, name:r.get(1)?,
              extension:r.get(2)?,bytes:r.get(3)?,file_count:r.get(4)?,is_directory:r.get(5)? }))
    }

    pub fn scan_history(&self, volume_id: Option<&str>, limit: usize) -> Result<Vec<ScanRecord>> {
        rows(&self.open()?, "SELECT id,volume,started,finished,outcome,items,issues,identity_ms,added_count,
          modified_count,removed_count,moved_count,bytes_delta FROM scans WHERE (?1 IS NULL OR volume=?1)
          ORDER BY id DESC LIMIT ?2", params![volume_id,sql_limit(limit)], |r| Ok(ScanRecord {
            id:r.get(0)?, volume:r.get(1)?, started:r.get(2)?, finished:r.get(3)?, outcome:r.get(4)?,
            items:r.get(5)?, issues:r.get(6)?, identity_ms:r.get(7)?, added_count:r.get(8)?, modified_count:r.get(9)?,
            removed_count:r.get(10)?, moved_count:r.get(11)?, bytes_delta:r.get(12)?,
        }))
    }

    pub fn scan_changes(&self, scan_id: i64, limit: usize) -> Result<Vec<ScanChange>> {
        rows(&self.open()?, "SELECT scan_id,volume,rel,kind,old_bytes,new_bytes FROM scan_changes
          WHERE scan_id=?1 ORDER BY kind,rel LIMIT ?2", params![scan_id,sql_limit(limit)], |r| Ok(ScanChange {
              scan_id:r.get(0)?, volume:r.get(1)?, rel:r.get(2)?, kind:r.get(3)?, old_bytes:r.get(4)?, new_bytes:r.get(5)?,
          }))
    }

    pub fn get_annotation(&self, volume_id: &str, rel: &str) -> Result<FileAnnotation> {
        Ok(self.open()?.query_row("SELECT tags,note,updated FROM file_annotations WHERE volume=?1 AND rel=?2",
           params![volume_id,rel], |r| Ok(FileAnnotation { tags:r.get(0)?,note:r.get(1)?,updated:r.get(2)? }))
           .optional()?.unwrap_or_default())
    }

    pub fn save_annotation(&self, volume_id: &str, rel: &str, tags: &str, note: &str) -> Result<()> {
        let mut db = self.open()?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        tx.execute("INSERT INTO file_annotations VALUES(?1,?2,?3,?4,?5)
          ON CONFLICT(volume,rel) DO UPDATE SET tags=excluded.tags,note=excluded.note,updated=excluded.updated",
           params![volume_id,rel,tags,note,now_ms()])?;
        tx.execute("DELETE FROM file_tags WHERE volume=?1 AND rel=?2", params![volume_id,rel])?;
        for tag in tags.split(',').map(normalize_key).filter(|tag| !tag.is_empty()) {
            tx.execute("INSERT OR IGNORE INTO file_tags VALUES(?1,?2,?3)", params![tag,volume_id,rel])?;
        }
        let indexed_target: Option<(i64, String)> = tx
            .query_row(
                "SELECT id, name FROM files WHERE volume=?1 AND rel=?2 AND removed IS NULL",
                params![volume_id, rel],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()?;
        tx.commit()?;
        if let Some((file_id, name)) = indexed_target {
            let content_db_path = self.data_dir.join("content.sqlite3");
            if content_db_path.exists() {
                if let Ok(cdb) = Connection::open(&content_db_path) {
                    let _ = configure(&cdb);
                    let is_indexed: bool = cdb
                        .query_row(
                            "SELECT EXISTS(SELECT 1 FROM content_files WHERE file_id=?1 AND status=1)",
                            [file_id],
                            |r| r.get(0),
                        )
                        .unwrap_or(false);
                    if is_indexed {
                        let has_meta_fts: bool = cdb
                            .query_row(
                                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name='content_meta_fts')",
                                [],
                                |r| r.get(0),
                            )
                            .unwrap_or(false);
                        if has_meta_fts {
                            let clean_name = crate::content::tokenize_meta_text(&name);
                            let clean_path = crate::content::tokenize_meta_text(rel);
                            let clean_tags = crate::content::tokenize_tags_text(tags);
                            let _ = cdb.execute("DELETE FROM content_meta_fts WHERE rowid=?1", [file_id]);
                            let _ = cdb.execute(
                                "INSERT INTO content_meta_fts(rowid, name, path, tags) VALUES(?1, ?2, ?3, ?4)",
                                params![file_id, clean_name, clean_path, clean_tags],
                            );
                        }
                    }
                }
            }
        }
        Ok(())
    }

    pub fn find_possible_copies(&self, volume_id: &str, rel: &str) -> Result<Vec<FileEntry>> {
        rows(&self.open()?, &format!("SELECT {ENTRY_COLUMNS} FROM files s JOIN files f
          ON (f.bytes,f.modified,f.created)=(s.bytes,s.modified,s.created) JOIN volumes v ON v.id=f.volume
          WHERE s.volume=?1 AND s.rel=?2 AND s.bytes>0 AND f.volume<>s.volume AND f.removed IS NULL
          AND s.removed IS NULL ORDER BY f.stale,v.online DESC,f.id LIMIT 20"), params![volume_id,rel], file_entry)
    }

    pub fn get_file(&self, volume_id: &str, rel: &str) -> Result<Option<FileEntry>> {
        self.open()?.query_row(&format!("SELECT {ENTRY_COLUMNS} FROM files f JOIN volumes v ON v.id=f.volume
           WHERE f.volume=?1 AND f.rel=?2"), params![volume_id,rel], file_entry).optional()
    }

    pub fn load_saved_searches(&self) -> io::Result<Vec<SavedSearch>> {
        let legacy = self.data_dir.join("saved-searches.json");
        let mut searches = match read_json(&legacy) {
            Err(e) if e.kind()==io::ErrorKind::NotFound => match read_json(&self.data_dir.join("saved_searches.json")) {
                Err(e) if e.kind()==io::ErrorKind::NotFound => Vec::new(),
                result => result?,
            },
            result => result?,
        };
        validate_saved(&searches)?;
        searches.sort_by_key(|s| normalize_key(&s.name));
        Ok(searches)
    }

    pub fn save_saved_searches(&self, searches: &[SavedSearch]) -> io::Result<()> {
        validate_saved(searches)?;
        let mut sorted = searches.to_vec();
        sorted.sort_by_key(|s| normalize_key(&s.name));
        fs::create_dir_all(&self.data_dir)?;
        write_json(&self.data_dir.join("saved-searches.json"), &sorted)?;
        write_json(&self.data_dir.join("saved_searches.json"), &sorted)
    }

    /// Acquire the desktop instance lock before recovering scans and scratch files.
    /// Keep the returned file open for the lifetime of the application.
    pub fn start_session(&self) -> crate::EngineResult<fs::File> {
        fs::create_dir_all(&self.data_dir)?;
        let mut options=fs::OpenOptions::new();
        options.read(true).write(true).create(true).truncate(false);
        #[cfg(windows)] {
            use std::os::windows::fs::OpenOptionsExt;
            options.share_mode(0);
        }
        let lock=options.open(self.data_dir.join("instance.lock"))?;
        self.recover_interrupted_scans()?;
        for entry in fs::read_dir(&self.data_dir)? {
            let entry=entry?;
            let name=entry.file_name();
            let Some(name)=name.to_str().and_then(|s|s.strip_prefix("scan-")) else {continue;};
            let Some((id,suffix))=name.split_once('.') else {continue;};
            if id.len()==32 && id.bytes().all(|b|b.is_ascii_hexdigit())
                && matches!(suffix,"sqlite3"|"sqlite3-wal"|"sqlite3-shm"|"sqlite3-journal")
                && entry.file_type()?.is_file() {
                fs::remove_file(entry.path())?;
            }
        }
        Ok(lock)
    }
    pub fn recover_interrupted_scans(&self) -> Result<()> {
        let mut db = self.open()?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        tx.execute("UPDATE scans SET outcome='Interrupted',finished=?1 WHERE finished IS NULL", [now_ms()])?;
        tx.execute("UPDATE volumes SET outcome='Interrupted' WHERE outcome='Scanning'", [])?;
        tx.commit()
    }
}

fn validate_saved(searches: &[SavedSearch]) -> io::Result<()> {
    let mut names = HashSet::new();
    for search in searches {
        if search.name.is_empty() || search.name.trim() != search.name || !names.insert(normalize_key(&search.name)) {
            return Err(invalid("Invalid or duplicate saved search name"));
        }
        ParsedSearch::parse(&search.search_filter())?;
    }
    Ok(())
}

fn migrate(db: &mut Connection) -> Result<()> {
    const KEY: &str = "rust-compatibility-v1";
    let initialized = |db: &Connection| -> Result<bool> {
        db.query_row("SELECT EXISTS(SELECT 1 FROM catalog_meta WHERE key=?1)",[KEY],|r|r.get(0))
    };
    if initialized(db)? {return Ok(());}
    let tx=db.transaction_with_behavior(TransactionBehavior::Immediate)?;
    if !initialized(&tx)? {
        for (table,column,definition) in [
            ("files","removed","INTEGER"),
            ("volumes","host","TEXT NOT NULL DEFAULT ''"),
            ("volumes","removable","INTEGER NOT NULL DEFAULT 0"),
            ("volumes","note","TEXT NOT NULL DEFAULT ''"),
            ("volumes","freshness_days","INTEGER NOT NULL DEFAULT 0"),
            ("scans","added_count","INTEGER NOT NULL DEFAULT 0"),
            ("scans","modified_count","INTEGER NOT NULL DEFAULT 0"),
            ("scans","removed_count","INTEGER NOT NULL DEFAULT 0"),
            ("scans","moved_count","INTEGER NOT NULL DEFAULT 0"),
            ("scans","bytes_delta","INTEGER NOT NULL DEFAULT 0"),
        ] {
            let exists:bool=tx.query_row("SELECT EXISTS(SELECT 1 FROM pragma_table_info(?1) WHERE name=?2)",
                params![table,column],|r|r.get(0))?;
            if !exists {tx.execute_batch(&format!("ALTER TABLE {table} ADD COLUMN {column} {definition};"))?;}
        }
        let folders_done:bool=tx.query_row("SELECT EXISTS(SELECT 1 FROM catalog_meta WHERE key='folders-v1')",[],|r|r.get(0))?;
        if !folders_done {
            tx.execute_batch("CREATE TEMP TABLE missing_folders(volume TEXT,rel TEXT,modified INTEGER,seen INTEGER,
                PRIMARY KEY(volume,rel)) WITHOUT ROWID;
                WITH RECURSIVE parents(volume,rel,modified,seen) AS (
                  SELECT volume,folder_parent(rel),modified,seen FROM files WHERE (attributes & 16)=0 AND removed IS NULL
                  UNION ALL SELECT volume,folder_parent(rel),modified,seen FROM parents WHERE rel<>''
                ) INSERT OR IGNORE INTO missing_folders
                  SELECT p.volume,p.rel,p.modified,p.seen FROM parents p WHERE p.rel<>'' AND NOT EXISTS(
                    SELECT 1 FROM files f WHERE f.volume=p.volume AND f.rel=p.rel);
                INSERT OR IGNORE INTO catalog_meta VALUES('folders-v1','done');")?;
            let folders=rows(&tx,"SELECT volume,rel,modified,seen FROM missing_folders",[],|r| {
                Ok((r.get::<_,String>(0)?,r.get::<_,String>(1)?,r.get::<_,i64>(2)?,r.get::<_,i64>(3)?))
            })?;
            let mut insert=tx.prepare("INSERT INTO files(volume,rel,name,name_key,parent_key,extension,bytes,modified,created,attributes,seen)
                VALUES(?1,?2,?3,?4,?5,'',0,?6,?6,16,?7)")?;
            let mut prefix=tx.prepare("INSERT OR IGNORE INTO prefixes VALUES(?1,last_insert_rowid())")?;
            for (volume,rel,modified,seen) in folders {
                let name=rel.rsplit('\\').next().unwrap();
                let name_key=catalog_key(name);
                let parent_key=catalog_key(parent(&rel));
                insert.execute(params![volume,rel,name,name_key,parent_key,modified,seen])?;
                for token in crate::util::prefixes(&name_key,&parent_key) {prefix.execute([token])?;}
            }
        }
        let stats_done:bool=tx.query_row("SELECT EXISTS(SELECT 1 FROM catalog_meta WHERE key='folder-stats-v1')",[],|r|r.get(0))?;
        if !stats_done || !folders_done {
            let volumes:Vec<String>=rows(&tx,"SELECT id FROM volumes",[],|r|r.get(0))?;
            for volume in volumes {rebuild_folder_stats(&tx,&volume)?;}
            tx.execute("INSERT OR IGNORE INTO catalog_meta VALUES('folder-stats-v1','done')",[])?;
        }
        tx.execute("INSERT INTO catalog_meta VALUES(?1,'done')",[KEY])?;
    }
    tx.commit()
}
pub(crate) fn configure(db: &Connection) -> Result<()> {
    db.busy_timeout(Duration::from_secs(30))?;
    db.execute_batch("PRAGMA temp_store=MEMORY; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456;")?;
    db.create_scalar_function("path_key", 1, FunctionFlags::SQLITE_DETERMINISTIC, |ctx| Ok(catalog_key(&ctx.get::<String>(0)?)))
}

fn register_prefixes(db: &Connection, short: Vec<String>) -> Result<()> {
    db.create_scalar_function("matches_name_prefixes", 1, FunctionFlags::SQLITE_DETERMINISTIC, move |ctx| {
        let prefs = tokenize_prefixes(&ctx.get::<String>(0)?);
        Ok(short.iter().all(|term| prefs.contains(term)))
    })?;
    db.create_scalar_function("matches_name_only", 2, FunctionFlags::SQLITE_DETERMINISTIC, |ctx| {
        let name_key = ctx.get::<String>(0)?;
        let query = ctx.get::<String>(1)?;
        let mut short_prefs: Option<HashSet<String>> = None;
        for term in query.split_whitespace() {
            if term.chars().count() < 3 {
                let prefs = short_prefs.get_or_insert_with(|| tokenize_prefixes(&name_key).into_iter().collect());
                if !prefs.contains(term) {
                    return Ok(false);
                }
            } else if !name_key.contains(term) {
                return Ok(false);
            }
        }
        Ok(true)
    })
}

pub(crate) fn rows<T>(db: &Connection, sql: &str, params: impl Params, map: impl FnMut(&Row<'_>) -> Result<T>) -> Result<Vec<T>> {
    db.prepare(sql)?.query_map(params, map)?.collect()
}

pub(crate) fn file_entry(r: &Row<'_>) -> Result<FileEntry> {
    Ok(FileEntry { id:r.get(0)?,volume:r.get(1)?,rel:r.get(2)?,name:r.get(3)?,mount:r.get(4)?,label:r.get(5)?,
        online:r.get(6)?,stale:r.get(7)?,bytes:r.get(8)?,modified:r.get(9)?,created:r.get(10)?,seen:r.get(11)?,
        attributes:r.get(12)?,removed:r.get(13)?,extension:r.get(14)?,file_key:r.get(15)? })
}

pub(crate) fn sql_error(error: impl std::error::Error + Send + Sync + 'static) -> rusqlite::Error {
    rusqlite::Error::ToSqlConversionFailure(Box::new(error))
}
pub(crate) fn sql_limit(limit: usize) -> i64 { i64::try_from(limit).unwrap_or(i64::MAX) }

pub(crate) fn upsert_mounted(db: &Connection, volume: &Volume) -> Result<()> {
    db.execute("INSERT INTO volumes(id,mount,label,online,host,removable) VALUES(?1,?2,?3,1,?4,?5)
      ON CONFLICT(id) DO UPDATE SET mount=excluded.mount,label=excluded.label,online=1,host=excluded.host,removable=excluded.removable",
      params![volume.id,volume.mount,volume.label,volume.host,volume.removable])?;
    Ok(())
}

pub(crate) fn rebuild_folder_stats(db: &Connection, volume: &str) -> Result<()> {
    db.execute("DELETE FROM folder_stats WHERE volume=?1", [volume])?;
    db.execute("WITH RECURSIVE direct(rel,bytes,files,dirs) AS (
      SELECT folder_parent(rel),sum(CASE WHEN (attributes & 16)=0 THEN bytes ELSE 0 END),
      sum((attributes & 16)=0),sum((attributes & 16)<>0)
      FROM files WHERE volume=?1 AND removed IS NULL GROUP BY folder_parent(rel)
      ), ancestors(rel,bytes,files,dirs) AS (
      SELECT * FROM direct UNION ALL SELECT folder_parent(rel),bytes,files,dirs FROM ancestors WHERE rel<>''
      ), totals AS (
      SELECT * FROM ancestors UNION ALL SELECT rel,0,0,0 FROM files
       WHERE volume=?1 AND removed IS NULL AND (attributes & 16)<>0 UNION ALL SELECT '',0,0,0
      ) INSERT INTO folder_stats SELECT ?1,rel,sum(bytes),sum(files),sum(dirs) FROM totals GROUP BY rel", [volume])?;
    Ok(())
}

struct ParsedSearch {
    filter: SearchFilter, terms: Vec<String>, exclusions: Vec<String>, status: Option<String>, tag: Option<String>,
}
impl ParsedSearch {
    fn parse(filter: &SearchFilter) -> io::Result<Self> {
        let query = filter.query.replace('/', "\\").replace('\0', "");
        let mut parsed = Self { filter:filter.clone(), terms:Vec::new(), exclusions:Vec::new(), status:None, tag:None };
        let mut fields = HashSet::new();
        for (token, colon) in search_tokens(&query)? {
            let (field, value) = colon.map_or(("", ""), |i| (&token[..i], &token[i+1..]));
            let field = field.to_ascii_lowercase();
            let f = &mut parsed.filter;
            if matches!(field.as_str(), "ext"|"drive"|"min"|"max"|"after"|"before"|"online"|"removed"|"size"|"modified"|"status"|"tag") {
                let dedup = if field=="size" { format!("size{}",value.chars().next().unwrap_or_default()) } else { field.clone() };
                if value.trim().is_empty() || !fields.insert(dedup) { return Err(invalid(format!("Specify one value for {field}"))); }
            }
            match field.as_str() {
                "ext" => {
                    let ext = value.trim_start_matches('.');
                    if ext.is_empty() || ext.chars().any(|c| c.is_whitespace() || ".\\/:*?\"<>|".contains(c)) {
                        return Err(invalid("Invalid extension"));
                    }
                    f.extension = Some(ext.into());
                }
                "drive" => f.volume = Some(value.into()),
                "min" => f.min = Some(parse_size(value)?),
                "max" => f.max = Some(parse_size(value)?),
                "after" => f.from = Some(parse_date(value)?),
                "before" => f.through = Some(parse_date(value)?),
                "online" => f.online = Some(parse_bool(value)?),
                "removed" => f.include_removed = parse_bool(value)?,
                "size" => match value.as_bytes().first() {
                    Some(b'>') => f.min = Some(parse_size(&value[1..])?.checked_add(1).ok_or_else(||invalid("Invalid minimum size"))?),
                    Some(b'<') => f.max = Some(parse_size(&value[1..])?-1),
                    _ => { let size = parse_size(value)?; f.min=Some(size); f.max=Some(size); }
                },
                "modified" => match value.as_bytes().first() {
                    Some(b'>') => f.from = Some(parse_date(&value[1..])?),
                    Some(b'<') => f.through = Some(parse_date(&value[1..])?),
                    _ => {
                        let date = NaiveDate::parse_from_str(value,"%Y-%m-%d").map_err(|_|invalid("Invalid date"))?;
                        f.from = Some(date_ms(date)?);
                        f.through = Some(date_ms(date.succ_opt().ok_or_else(||invalid("Invalid date"))?)?);
                    }
                },
                "status" => {
                    let status = value.to_ascii_lowercase();
                    if !matches!(status.as_str(),"online"|"offline"|"removed"|"stale") { return Err(invalid("Unknown status")); }
                    if status=="removed" { f.include_removed=true; }
                    parsed.status=Some(status);
                }
                "tag" => parsed.tag=Some(normalize_key(value)),
                _ => {
                    let (term, terms) = match token.strip_prefix('-').filter(|s| !s.is_empty()) {
                        Some(term) => (term, &mut parsed.exclusions),
                        None => (token.as_str(), &mut parsed.terms),
                    };
                    for term in catalog_key(term).split_whitespace() {
                        if !terms.iter().any(|t| t==term) { terms.push(term.into()); }
                    }
                }
            }
        }
        if matches!((parsed.filter.min,parsed.filter.max),(Some(a),Some(b)) if a>b)
            || matches!((parsed.filter.from,parsed.filter.through),(Some(a),Some(b)) if a>=b) {
            return Err(invalid("Invalid search interval"));
        }
        Ok(parsed)
    }
}

fn search_tokens(input: &str) -> io::Result<Vec<(String, Option<usize>)>> {
    let mut tokens = Vec::new();
    let mut token = String::new();
    let mut quoted = false;
    let mut colon = None;
    let mut chars = input.chars().peekable();
    while let Some(c) = chars.next() {
        if c=='"' {
            if quoted && chars.next_if_eq(&'"').is_some() { token.push('"'); }
            else { quoted = !quoted; }
        } else if !quoted && c.is_whitespace() {
            if !token.is_empty() { tokens.push((std::mem::take(&mut token),colon.take())); }
        } else {
            if c==':' && !quoted && colon.is_none() { colon=Some(token.len()); }
            token.push(c);
        }
    }
    if quoted { return Err(invalid("Close the quoted search text")); }
    if !token.is_empty() { tokens.push((token,colon)); }
    Ok(tokens)
}

fn parse_bool(value: &str) -> io::Result<bool> {
    value.to_ascii_lowercase().parse().map_err(|_|invalid("Use true or false"))
}

fn date_ms(date: NaiveDate) -> io::Result<i64> {
    Local.from_local_datetime(&date.and_hms_opt(0,0,0).unwrap()).latest()
        .map(|date|date.timestamp_millis()).ok_or_else(||invalid("Invalid local date"))
}
fn parse_date(value: &str) -> io::Result<i64> {
    date_ms(NaiveDate::parse_from_str(value,"%Y-%m-%d").map_err(|_|invalid("Use yyyy-MM-dd"))?)
}

fn parse_size(value: &str) -> io::Result<i64> {
    let split = value.find(|c:char| !c.is_ascii_digit() && c!='.').unwrap_or(value.len());
    let (number,unit) = value.split_at(split);
    let multiplier = match unit.to_ascii_lowercase().as_str() {
        ""|"b"=>1, "kb"=>1_000, "mb"=>1_000_000, "gb"=>1_000_000_000, "tb"=>1_000_000_000_000,
        "kib"=>1<<10, "mib"=>1<<20, "gib"=>1<<30, "tib"=>1i128<<40,
        _=>return Err(invalid("Invalid size unit")),
    };
    let wrong = || invalid("Size must resolve to a whole number of bytes");
    let (integer,fraction) = number.split_once('.').unwrap_or((number,""));
    if number.ends_with('.') { return Err(wrong()); }
    let base = integer.parse::<i128>().ok().and_then(|n|n.checked_mul(multiplier)).ok_or_else(wrong)?;
    let fraction = fraction.trim_end_matches('0');
    let extra = if fraction.is_empty() { 0 } else {
        let divisor = 10i128.checked_pow(fraction.len() as u32).ok_or_else(wrong)?;
        let scaled = fraction.parse::<i128>().ok().and_then(|n|n.checked_mul(multiplier)).ok_or_else(wrong)?;
        if scaled % divisor!=0 { return Err(wrong()); }
        scaled/divisor
    };
    base.checked_add(extra).and_then(|n|i64::try_from(n).ok()).ok_or_else(wrong)
}

#[cfg(not(windows))]
pub(crate) fn mounted_volumes() -> Vec<Volume> { Vec::new() }

#[cfg(windows)]
pub(crate) fn mounted_volumes() -> Vec<Volume> {
    use windows_sys::Win32::{Foundation::INVALID_HANDLE_VALUE, Storage::FileSystem::*};
    use std::collections::BTreeMap;
    let mut volumes = BTreeMap::new();
    let mut add = |mount: String, expected: Option<&str>| {
        let Some((id,label,removable)) = windows_volume(&mount) else { return };
        if expected.is_some_and(|expected| expected!=id) { return; }
        volumes.entry(id.to_ascii_lowercase()).or_insert_with(||Volume {
            id, mount, label, online:true, removable, hours:24.0, host:std::env::var("COMPUTERNAME").unwrap_or_default(),
            ..Volume::default()
        });
    };
    // The API supplies UTF-16 volume names and a multistring of mount roots.
    unsafe {
        let mut buffer = [0u16;128];
        let search = FindFirstVolumeW(buffer.as_mut_ptr(),buffer.len() as u32);
        if search!=INVALID_HANDLE_VALUE {
            loop {
                let id = wide_string(&buffer);
                let mut paths = vec![0;1024];
                let mut needed = 0;
                if GetVolumePathNamesForVolumeNameW(buffer.as_ptr(),paths.as_mut_ptr(),paths.len() as u32,&mut needed)==0
                    && (1025..65536).contains(&needed) {
                    paths.resize(needed as usize,0);
                    GetVolumePathNamesForVolumeNameW(buffer.as_ptr(),paths.as_mut_ptr(),needed,&mut needed);
                }
                let mut mounts: Vec<_> = paths.split(|&c|c==0).take_while(|s|!s.is_empty()).map(String::from_utf16_lossy).collect();
                mounts.sort_by_key(|m|(m.len(),m.to_ascii_lowercase()));
                if let Some(mount) = mounts.into_iter().find(|m| windows_volume(m).is_some_and(|v|v.0==id)) {
                    add(mount,Some(&id));
                }
                if FindNextVolumeW(search,buffer.as_mut_ptr(),buffer.len() as u32)==0 { break; }
            }
            FindVolumeClose(search);
        }
        let mask = GetLogicalDrives();
        for letter in 0..26 {
            if mask & (1<<letter)==0 { continue; }
            let mount = format!("{}:\\", (b'A'+letter) as char);
            let wide = wide_null(&mount);
            if matches!(GetDriveTypeW(wide.as_ptr()),2|3|5) { add(mount,None); }
        }
    }
    volumes.into_values().collect()
}

#[cfg(windows)]
pub(crate) fn wide_null(s: &str) -> Vec<u16> { s.encode_utf16().chain([0]).collect() }
#[cfg(windows)]
fn wide_string(s: &[u16]) -> String { String::from_utf16_lossy(&s[..s.iter().position(|&c|c==0).unwrap_or(s.len())]) }

#[cfg(windows)]
pub(crate) fn windows_volume(mount: &str) -> Option<(String,String,bool)> {
    use std::ptr::null_mut;
    use windows_sys::Win32::{Storage::FileSystem::*, System::Diagnostics::Debug::SetThreadErrorMode};
    let wide = wide_null(mount);
    let mut label = [0;261];
    let mut fs = [0;64];
    let mut guid = [0;128];
    let mut serial = 0;
    let mut previous = 0;
    // Suppress Windows removable-media error dialogs on this thread.
    unsafe {
        let restore = SetThreadErrorMode(1,&mut previous)!=0;
        let good = GetVolumeInformationW(wide.as_ptr(),label.as_mut_ptr(),label.len() as u32,
            &mut serial,null_mut(),null_mut(),fs.as_mut_ptr(),fs.len() as u32)!=0;
        let has_guid = good && GetVolumeNameForVolumeMountPointW(wide.as_ptr(),guid.as_mut_ptr(),guid.len() as u32)!=0;
        if restore { SetThreadErrorMode(previous,null_mut()); }
        if !good || (!has_guid && serial==0) { return None; }
        let id = if has_guid { wide_string(&guid) } else {
            format!("serial:{serial:08X}:{}:{}",wide_string(&fs).to_uppercase(),mount.to_uppercase())
        };
        let label = if label[0]==0 { mount.to_owned() } else { wide_string(&label) };
        Some((id,label,GetDriveTypeW(wide.as_ptr())==2))
    }
}




