use std::{
    collections::{HashMap, HashSet},
    fs::{self, File},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::{atomic::AtomicBool, Arc},
    thread,
};
use crossbeam_channel::bounded;
use rusqlite::{named_params, params, Connection, OptionalExtension, TransactionBehavior};

use crate::{
    catalog::{configure, rows, sql_limit, Catalog},
    content_extract::{self, OFFICE, TEXT},
    content_query::{self, FacetChip, Node, QueryFacets, quote},
    scanner::{check_cancel, RECALL_RISK},
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
CREATE TABLE IF NOT EXISTS content_meta(
    key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS content_fts
USING fts5(body, content='', contentless_delete=1, tokenize='porter unicode61 remove_diacritics 2');
CREATE VIRTUAL TABLE IF NOT EXISTS content_meta_fts
USING fts5(name, path, tags, content='', contentless_delete=1, tokenize='porter unicode61 remove_diacritics 2');";

const MAX_FIELD: usize = 64 << 20;
const EXTRACTOR: i32 = 1;
const RRF_K: f64 = 60.0;

const FACET_WHERE_SQL: &str = "
  AND ($volume IS NULL OR f.volume = $volume)
  AND ($ext IS NULL OR f.extension = $ext)
  AND ($facet_exts IS NULL OR f.extension IN (SELECT value FROM json_each($facet_exts)))
  AND ($min_bytes IS NULL OR f.bytes >= $min_bytes)
  AND ($max_bytes IS NULL OR f.bytes <= $max_bytes)
  AND ($mod_after IS NULL OR f.modified >= $mod_after)
  AND ($mod_before IS NULL OR f.modified < $mod_before)
  AND ($online IS NULL OR v.online = $online)
  AND ($tags_json IS NULL OR NOT EXISTS (
      SELECT 1 FROM json_each($tags_json) j
      WHERE NOT EXISTS (
          SELECT 1 FROM source_catalog.file_tags t
          WHERE t.tag = j.value AND t.volume = f.volume AND t.rel = f.rel
      )
  ))
  AND ($excl_ids IS NULL OR c.file_id NOT IN (SELECT value FROM json_each($excl_ids)))";

#[derive(Copy, Clone, Debug, Default, Eq, PartialEq)]
pub enum ContentScanMode {
    #[default]
    Eco,
    Balanced,
    Turbo,
}

#[derive(Clone, Debug)]
pub struct ContentScanOptions {
    pub volume_ids: Option<Vec<String>>,
    pub mode: ContentScanMode,
    pub max_file_bytes: i64,
    pub max_text_chars: usize,
    pub store_snippets: bool,
    pub include_text: bool,
    pub include_office: bool,
    pub include_pdf: bool,
    pub skip_system_folders: bool,
}
impl Default for ContentScanOptions {
    fn default() -> Self {
        Self {
            volume_ids: None,
            mode: ContentScanMode::Eco,
            max_file_bytes: 25 << 20,
            max_text_chars: 131_072,
            store_snippets: true,
            include_text: true,
            include_office: true,
            include_pdf: true,
            skip_system_folders: true,
        }
    }
}

#[derive(Clone, Debug, Default)]
pub struct ContentScanEstimate {
    pub eligible_files: u64,
    pub already_indexed: u64,
    pub to_scan: u64,
    pub total_source_bytes: u64,
    pub estimated_index_bytes: u64,
}

#[derive(Clone, Debug, Default)]
pub struct ContentScanProgress {
    pub processed: u64,
    pub total: u64,
    pub indexed: u64,
    pub skipped: u64,
    pub failed: u64,
    pub current_file: String,
}

#[derive(Copy, Clone, Debug, Default, Eq, PartialEq)]
pub enum ContentMatchMode {
    #[default]
    Smart,
    AllWords,
    AnyWord,
    ExactPhrase,
}

#[derive(Clone, Debug)]
pub struct ContentSearchOptions {
    pub mode: ContentMatchMode,
    pub fuzzy_matching: bool,
}
impl Default for ContentSearchOptions {
    fn default() -> Self {
        Self {
            mode: ContentMatchMode::Smart,
            fuzzy_matching: true,
        }
    }
}

#[derive(Clone, Debug)]
pub struct ContentSearchHit {
    pub file_id: i64,
    pub volume: String,
    pub rel: String,
    pub name: String,
    pub full_path: PathBuf,
    pub volume_label: String,
    pub online: bool,
    pub bytes: i64,
    pub modified: i64,
    pub snippet: String,
    pub rank: f64,
    pub match_reason: String,
    pub match_tone: String,
}

#[derive(Clone, Debug, Default)]
pub struct ContentSearchResult {
    pub hits: Vec<ContentSearchHit>,
    pub summary: String,
    pub fuzzy_corrections: Vec<String>,
    pub used_relaxed_match: bool,
    pub facet_chips: Vec<FacetChip>,
}

#[derive(Clone, Debug, Default)]
pub struct ContentIndexStats {
    pub indexed_files: u64,
    pub total_text_bytes: u64,
    pub database_bytes: u64,
}

#[derive(Clone, Debug)]
pub struct ContentIndex {
    pub catalog: Catalog,
}

struct Record {
    volume: String,
    rel: String,
    bytes: i64,
    modified: i64,
    version: i32,
    status: i32,
    indexed_at: i64,
    snippet: Option<Vec<u8>>,
    text_bytes: i32,
    text_cap: i32,
}

struct Source {
    id: i64,
    volume: String,
    rel: String,
    mount: String,
    extension: String,
    bytes: i64,
    modified: i64,
    status: Option<i32>,
    snippet: bool,
    cap: usize,
}

impl Source {
    fn needs_scan(&self, options: &ContentScanOptions) -> bool {
        match self.status {
            Some(1) => self.snippet != options.store_snippets || self.cap != options.max_text_chars,
            Some(2) => self.cap != options.max_text_chars,
            _ => true,
        }
    }
}

struct SqlFacetArgs<'a> {
    volume: Option<&'a str>,
    explicit_ext: Option<&'a str>,
    facet_exts_json: Option<&'a str>,
    min_bytes: Option<i64>,
    max_bytes: Option<i64>,
    mod_after: Option<i64>,
    mod_before: Option<i64>,
    online: Option<bool>,
    tags_json: Option<&'a str>,
    excl_ids_json: Option<&'a str>,
}

struct HydratedRow {
    file_id: i64,
    volume: String,
    rel: String,
    name: String,
    full_path: PathBuf,
    volume_label: String,
    online: bool,
    bytes: i64,
    modified: i64,
    tags: String,
    compressed_body: Option<Vec<u8>>,
}

struct Candidate {
    row: HydratedRow,
    body_rank: Option<usize>,
    meta_rank: Option<usize>,
    sem_rank: Option<(usize, f32)>,
    decompressed_body: Option<String>,
    score: f64,
    any_covered: usize,
    match_reason: &'static str,
    match_tone: &'static str,
}

impl ContentIndex {
    pub fn new(catalog: Catalog) -> Self {
        Self { catalog }
    }

    pub fn database_path(&self) -> PathBuf {
        self.catalog.data_dir.join("content.sqlite3")
    }

    pub fn open(&self) -> EngineResult<Connection> {
        fs::create_dir_all(&self.catalog.data_dir)?;
        let db = Connection::open(self.database_path())?;
        configure(&db)?;
        db.execute_batch(SCHEMA)?;
        db.execute_batch(crate::semantic::VECTOR_SCHEMA)?;
        Ok(db)
    }

    fn attached(&self) -> EngineResult<Connection> {
        self.catalog.open()?;
        let db = self.open()?;
        db.execute(
            "ATTACH DATABASE ?1 AS source_catalog",
            [self.catalog.database_path().to_string_lossy().as_ref()],
        )?;
        Ok(db)
    }

    fn visit_candidates(
        &self,
        db: &Connection,
        options: &ContentScanOptions,
        mut visit: impl FnMut(Source) -> EngineResult<()>,
    ) -> EngineResult<()> {
        if options.max_text_chars == 0
            || options.max_text_chars > MAX_FIELD / 3
            || options.max_file_bytes < 0
        {
            return Err(invalid("Invalid content scan limits").into());
        }
        let extensions = TEXT
            .split_ascii_whitespace()
            .filter(|_| options.include_text)
            .chain(OFFICE.split_ascii_whitespace().filter(|_| options.include_office))
            .chain(["pdf"].into_iter().filter(|_| options.include_pdf))
            .collect::<Vec<_>>();
        let volumes = options.volume_ids.as_ref().map(serde_json::to_string).transpose()?;
        let mut statement = db.prepare(
            "SELECT f.id,f.volume,f.rel,v.mount,f.extension,f.bytes,f.modified,c.status,
                 c.brotli_text IS NOT NULL,coalesce(s.text_cap,0)
              FROM source_catalog.files f JOIN source_catalog.volumes v ON v.id=f.volume
              LEFT JOIN content_files c ON c.file_id=f.id AND (c.volume,c.rel,c.bytes,c.modified,c.extractor_ver)
                 =(f.volume,f.rel,f.bytes,f.modified,?1)
              LEFT JOIN content_sizes s ON s.file_id=c.file_id
              WHERE v.online=1 AND f.removed IS NULL AND (f.attributes & ?2)=0 AND f.bytes BETWEEN 0 AND ?3
                 AND f.extension IN (SELECT value FROM json_each(?4))
                 AND (?5 IS NULL OR f.volume IN (SELECT value FROM json_each(?5)))",
        )?;
        let mut rows = statement.query(params![
            EXTRACTOR,
            RECALL_RISK | 16,
            options.max_file_bytes,
            serde_json::to_string(&extensions)?,
            volumes
        ])?;
        while let Some(r) = rows.next()? {
            let source = Source {
                id: r.get(0)?,
                volume: r.get(1)?,
                rel: r.get(2)?,
                mount: r.get(3)?,
                extension: r.get(4)?,
                bytes: r.get(5)?,
                modified: r.get(6)?,
                status: r.get(7)?,
                snippet: r.get(8)?,
                cap: r.get(9)?,
            };
            if !options.skip_system_folders || !noise_path(&source.rel) {
                visit(source)?;
            }
        }
        Ok(())
    }

    pub fn estimate_scan(&self, options: &ContentScanOptions) -> EngineResult<ContentScanEstimate> {
        let db = self.attached()?;
        let mut estimate = ContentScanEstimate::default();
        let mut pending_text = 0u64;
        self.visit_candidates(&db, options, |source| {
            estimate.eligible_files += 1;
            estimate.total_source_bytes =
                estimate.total_source_bytes.saturating_add(source.bytes as u64);
            if source.status == Some(1) {
                estimate.already_indexed += 1;
            }
            if source.needs_scan(options) {
                estimate.to_scan += 1;
                pending_text = pending_text
                    .saturating_add((source.bytes as u64).min(options.max_text_chars as u64));
            }
            Ok(())
        })?;
        estimate.estimated_index_bytes = if options.store_snippets {
            pending_text.div_ceil(5)
        } else {
            pending_text.saturating_mul(2).div_ceil(25)
        };
        Ok(estimate)
    }

    pub fn scan(
        &self,
        options: &ContentScanOptions,
        cancel: Arc<AtomicBool>,
        mut report: impl FnMut(&ContentScanProgress),
    ) -> EngineResult<ContentScanProgress> {
        check_cancel(&cancel)?;
        let mut db = self.attached()?;
        ensure_meta_fts_backfill(&mut db)?;
        let active_pack = crate::semantic::load_pack_setting(&self.catalog.data_dir);
        let active_embedder = if active_pack.is_enabled() {
            crate::semantic::load_embedder(&self.catalog.data_dir, active_pack).ok()
        } else {
            None
        };
        let source_db = self.attached()?;
        source_db.execute_batch("BEGIN")?;
        let mut progress = ContentScanProgress::default();
        self.visit_candidates(&source_db, options, |_| {
            check_cancel(&cancel)?;
            progress.total += 1;
            Ok(())
        })?;
        report(&progress);
        let cpus = thread::available_parallelism().map_or(2, usize::from);
        let workers = match options.mode {
            ContentScanMode::Eco => (cpus / 4).clamp(1, 2),
            ContentScanMode::Balanced => (cpus / 2).clamp(1, 12),
            ContentScanMode::Turbo => cpus.clamp(1, 32),
        }
        .min(progress.total as usize);
        thread::scope(|scope| -> EngineResult<()> {
            let (work_tx, work_rx) = bounded(workers.max(1) * 4);
            let (result_tx, result_rx) = bounded(workers.max(1) * 2);
            let producer_cancel = &cancel;
            let producer = scope.spawn(move || {
                self.visit_candidates(&source_db, options, |source| {
                    check_cancel(producer_cancel)?;
                    work_tx.send(source).map_err(|_| crate::util::interrupted())?;
                    Ok(())
                })
            });
            for _ in 0..workers {
                let work_rx = work_rx.clone();
                let result_tx = result_tx.clone();
                let cancel = &cancel;
                let embedder = active_embedder.clone();
                scope.spawn(move || {
                    if options.mode == ContentScanMode::Eco {
                        background_priority();
                    }
                    while let Ok(source) = work_rx.recv() {
                        if check_cancel(cancel).is_err() {
                            break;
                        }
                        if !source.needs_scan(options) {
                            if result_tx.send((source.rel, Ok(None))).is_err() {
                                break;
                            }
                            continue;
                        }
                        let body = content_extract::safe_source_path(
                            &source.volume,
                            &source.mount,
                            &source.rel,
                        )
                        .map_err(Into::into)
                        .and_then(|path| {
                            let file =
                                content_extract::open_safe(&path, source.bytes, source.modified)?;
                            content_extract::extract(
                                file,
                                &source.extension,
                                options.max_text_chars,
                                cancel,
                            )
                        });
                        let result = body.and_then(|body| {
                            let status = if body.is_empty() { 2 } else { 1 };
                            let vec = if status == 1 {
                                embedder.as_deref().and_then(|emb| {
                                    crate::semantic::encode_document(emb, "", &source.rel, &body)
                                })
                            } else {
                                None
                            };
                            let record = Record {
                                volume: source.volume,
                                rel: source.rel.clone(),
                                bytes: source.bytes,
                                modified: source.modified,
                                version: EXTRACTOR,
                                status,
                                indexed_at: now_ms(),
                                snippet: if options.store_snippets && !body.is_empty() {
                                    Some(compress(body.as_bytes())?)
                                } else {
                                    None
                                },
                                text_bytes: body.len() as i32,
                                text_cap: options.max_text_chars as i32,
                            };
                            Ok(Some((source.id, record, body, vec)))
                        });
                        if result_tx.send((source.rel, result)).is_err() {
                            break;
                        }
                    }
                });
            }
            drop(work_rx);
            drop(result_tx);
            while let Ok(first) = result_rx.recv() {
                let batch = std::iter::once(first).chain(result_rx.try_iter().take(31));
                let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
                for (rel, result) in batch {
                    check_cancel(&cancel)?;
                    progress.processed += 1;
                    progress.current_file = rel;
                    match result {
                        Ok(Some((id, record, body, vec))) => {
                            if lookup(&tx, &record)? != Some(id) {
                                progress.skipped += 1;
                                continue;
                            }
                            write(&tx, id, &record, &body)?;
                            if record.status == 1 {
                                if let Some(ref v) = vec {
                                    crate::semantic::upsert_vector(
                                        &tx,
                                        id,
                                        active_pack,
                                        v,
                                        record.indexed_at,
                                    )?;
                                }
                                progress.indexed += 1;
                            } else {
                                let _ = tx.execute(
                                    "DELETE FROM content_vectors WHERE file_id = ?1",
                                    [id],
                                );
                                progress.skipped += 1;
                            }
                        }
                        Ok(None) => progress.skipped += 1,
                        Err(_) => progress.failed += 1,
                    }
                }
                if progress.indexed > 0 {
                    tx.execute(
                        "INSERT OR REPLACE INTO content_meta(key, value) VALUES('meta_fts_v1', '1')",
                        [],
                    )?;
                }
                tx.commit()?;
                report(&progress);
            }
            check_cancel(&cancel)?;
            producer.join().map_err(|_| invalid("Content producer failed"))??;
            Ok(())
        })?;
        if progress.indexed > 0 {
            optimize(&db)?;
        }
        Ok(progress)
    }

    pub fn optimize(&self) -> EngineResult<()> {
        optimize(&self.open()?)
    }

    pub fn stats(&self) -> EngineResult<ContentIndexStats> {
        let db = self.open()?;
        let (indexed_files, total_text_bytes) = db.query_row(
            "SELECT count(*),coalesce(sum(s.text_bytes),0)
              FROM content_files c LEFT JOIN content_sizes s USING(file_id) WHERE c.status=1",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )?;
        let path = self.database_path();
        let wal = path.with_extension("sqlite3-wal");
        Ok(ContentIndexStats {
            indexed_files,
            total_text_bytes,
            database_bytes: fs::metadata(path)?.len() + fs::metadata(wal).map_or(0, |m| m.len()),
        })
    }

    pub fn search(
        &self,
        query: &str,
        volume: Option<&str>,
        extension: Option<&str>,
        limit: usize,
        options: &ContentSearchOptions,
    ) -> EngineResult<ContentSearchResult> {
        if query.trim().is_empty() || limit == 0 {
            return Ok(ContentSearchResult::default());
        }
        let invalid_query = || ContentSearchResult {
            summary: "Enter a valid search with at least one positive term".into(),
            ..Default::default()
        };
        let now = now_ms();
        let Some(parsed) = content_query::parse_with_now(query, options.mode, now) else {
            return Ok(invalid_query());
        };

        let facets: QueryFacets = parsed.facets;
        let facet_chips = facets.chips.clone();
        let explicit_ext = extension.map(|s| normalize_key(s.trim_start_matches('.')));
        let facet_exts_json = (!facets.extensions.is_empty())
            .then(|| serde_json::to_string(&facets.extensions))
            .transpose()?;
        let tags_json = (!facets.tags.is_empty())
            .then(|| serde_json::to_string(&facets.tags))
            .transpose()?;

        let mut db = self.attached()?;
        ensure_meta_fts_backfill(&mut db)?;
        db.execute_batch("BEGIN")?;

        // Facet-only query support (e.g. "large pdfs from last week", "ext:pdf #urgent")
        let Some(root) = parsed.root else {
            if facets.is_empty() {
                return Ok(invalid_query());
            }
            let facet_args = SqlFacetArgs {
                volume,
                explicit_ext: explicit_ext.as_deref(),
                facet_exts_json: facet_exts_json.as_deref(),
                min_bytes: facets.min_bytes,
                max_bytes: facets.max_bytes,
                mod_after: facets.modified_after,
                mod_before: facets.modified_before,
                online: facets.online,
                tags_json: tags_json.as_deref(),
                excl_ids_json: None,
            };
            let rows = query_facet_only(&db, &facet_args, limit)?;
            let hits = rows
                .into_iter()
                .enumerate()
                .map(|(idx, row)| {
                    let snippet = row
                        .compressed_body
                        .as_deref()
                        .and_then(|compressed| decompress(compressed).ok())
                        .map(|body| crate::fuzzy::extract_snippet(&body, &[], 240, false).text)
                        .unwrap_or_default();
                    ContentSearchHit {
                        file_id: row.file_id,
                        volume: row.volume,
                        rel: row.rel,
                        name: row.name,
                        full_path: row.full_path,
                        volume_label: row.volume_label,
                        online: row.online,
                        bytes: row.bytes,
                        modified: row.modified,
                        snippet,
                        rank: 1.0 / (RRF_K + (idx + 1) as f64),
                        match_reason: "Facet".into(),
                        match_tone: "accent".into(),
                    }
                })
                .collect();
            return Ok(ContentSearchResult {
                hits,
                summary: String::new(),
                fuzzy_corrections: Vec::new(),
                used_relaxed_match: false,
                facet_chips,
            });
        };

        let mut positive = Vec::new();
        root.positive(false, &mut positive);
        if positive.is_empty() {
            return Ok(invalid_query());
        }

        // Collect global required exclusions across body, meta, and semantic vector sources
        let mut required_exclusions = Vec::new();
        if let Some(ref smart) = parsed.natural {
            required_exclusions.extend(smart.excluded.iter().cloned());
        } else {
            extract_required_exclusions(&root, &mut required_exclusions);
        }
        let mut excluded_ids_set = HashSet::<i64>::new();
        if !required_exclusions.is_empty() {
            let excl_union = required_exclusions
                .into_iter()
                .reduce(|a, b| a.join(b, true))
                .unwrap();
            let (excl_expr, excl_comp) = excl_union.compile(&HashMap::new(), true);
            if !excl_comp && !excl_expr.is_empty() {
                for id in rows(
                    &db,
                    "SELECT rowid FROM content_fts WHERE content_fts MATCH ?1",
                    [&excl_expr],
                    |r| r.get::<_, i64>(0),
                )? {
                    excluded_ids_set.insert(id);
                }
                for id in rows(
                    &db,
                    "SELECT rowid FROM content_meta_fts WHERE content_meta_fts MATCH ?1",
                    [&excl_expr],
                    |r| r.get::<_, i64>(0),
                )? {
                    excluded_ids_set.insert(id);
                }
            }
        }
        let excl_ids_json = (!excluded_ids_set.is_empty())
            .then(|| {
                let vec: Vec<i64> = excluded_ids_set.iter().copied().collect();
                serde_json::to_string(&vec)
            })
            .transpose()?;

        // Fast cached SymSpell typo expansion (O(1) memory cache + noisy typo override for 1..=4 docs)
        let mut corrections: HashMap<String, Vec<String>> = HashMap::new();
        if options.fuzzy_matching {
            let (indexed_files, latest_indexed_at): (u64, i64) = db.query_row(
                "SELECT count(*), coalesce(max(indexed_at), 0) FROM content_files WHERE status = 1",
                [],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )?;
            if indexed_files > 0 {
                let db_key = self.database_path().to_string_lossy().into_owned();
                let vocab =
                    crate::fuzzy::cached_vocab(&db, &db_key, indexed_files, latest_indexed_at);
                let mut seen = HashSet::new();
                for word in positive
                    .iter()
                    .filter(|t| !t.prefix && !t.verbatim)
                    .flat_map(|t| words(&t.text))
                    .map(str::to_lowercase)
                    .filter(|w| seen.insert(w.clone()))
                    .take(32)
                {
                    let len = word.chars().count();
                    if !(3..=64).contains(&len) || word.chars().all(char::is_numeric) {
                        continue;
                    }
                    let mut exact_doc_freq = vocab.doc_freq(&word);
                    if exact_doc_freq < 5 {
                        let exact_q = quote(&word, false);
                        let body_cap5: u64 = db.query_row(
                            "SELECT count(*) FROM (SELECT 1 FROM content_fts WHERE content_fts MATCH ?1 LIMIT 5)",
                            [&exact_q],
                            |r| r.get(0),
                        )?;
                        let meta_cap5: u64 = if body_cap5 < 5 {
                            db.query_row(
                                "SELECT count(*) FROM (SELECT 1 FROM content_meta_fts WHERE content_meta_fts MATCH ?1 LIMIT 5)",
                                [&exact_q],
                                |r| r.get(0),
                            )?
                        } else {
                            0
                        };
                        exact_doc_freq = exact_doc_freq.max(body_cap5.saturating_add(meta_cap5));
                    }
                    let suggestions = vocab.suggest(&word, exact_doc_freq, 4);
                    if !suggestions.is_empty() {
                        corrections.insert(
                            word,
                            suggestions.into_iter().map(|m| m.term).collect(),
                        );
                    }
                }
            }
        }

        let facet_args = SqlFacetArgs {
            volume,
            explicit_ext: explicit_ext.as_deref(),
            facet_exts_json: facet_exts_json.as_deref(),
            min_bytes: facets.min_bytes,
            max_bytes: facets.max_bytes,
            mod_after: facets.modified_after,
            mod_before: facets.modified_before,
            online: facets.online,
            tags_json: tags_json.as_deref(),
            excl_ids_json: excl_ids_json.as_deref(),
        };

        let pool = limit.saturating_mul(3).max(200);
        let (fts_expr, fts_comp) = root.compile(&corrections, false);

        // 1. Retrieve from content_fts (body BM25)
        let body_ids = query_fts_ranked(
            &db,
            "content_fts",
            "bm25(content_fts)",
            &fts_expr,
            fts_comp,
            &facet_args,
            pool,
        )?;

        // 2. Retrieve from content_meta_fts (name, path, tags BM25)
        let meta_ids = query_fts_ranked(
            &db,
            "content_meta_fts",
            "bm25(content_meta_fts, 3.0, 1.2, 2.5)",
            &fts_expr,
            fts_comp,
            &facet_args,
            pool,
        )?;

        // 3. Optional Model2Vec Semantic Vector Search (in Smart mode when pack enabled & installed)
        let mut sem_hits: Vec<(i64, f32)> = Vec::new();
        if options.mode == ContentMatchMode::Smart {
            let pack = crate::semantic::load_pack_setting(&self.catalog.data_dir);
            if pack.is_enabled() && crate::semantic::vector_count(&db, pack) > 0 {
                let sem_query = build_semantic_query_text(&parsed.cleaned_query, parsed.natural.as_ref());
                if let Ok(Some(qvec)) =
                    crate::semantic::encode_query(&self.catalog.data_dir, pack, &sem_query)
                {
                    let raw_sem = crate::semantic::search_vectors(
                        &db,
                        pack,
                        &qvec,
                        crate::semantic::DEFAULT_MIN_COSINE,
                        pool.saturating_mul(2),
                    )?;
                    let filtered_raw: Vec<(i64, f32)> = raw_sem
                        .into_iter()
                        .filter(|(id, _)| !excluded_ids_set.contains(id))
                        .collect();
                    if !filtered_raw.is_empty() {
                        let valid_sem = filter_semantic_candidates(&db, &filtered_raw, &facet_args)?;
                        sem_hits = filtered_raw
                            .into_iter()
                            .filter(|(id, _)| valid_sem.contains(id))
                            .take(pool)
                            .collect();
                    }
                }
            }
        }

        // Union candidate IDs across all 3 sources
        let mut body_pos_map = HashMap::<i64, usize>::with_capacity(body_ids.len());
        let mut meta_pos_map = HashMap::<i64, usize>::with_capacity(meta_ids.len());
        let mut sem_pos_map = HashMap::<i64, (usize, f32)>::with_capacity(sem_hits.len());
        let mut ordered_cand_ids = Vec::<i64>::new();
        let mut seen_cands = HashSet::<i64>::new();

        for (i, &id) in body_ids.iter().enumerate() {
            body_pos_map.insert(id, i + 1);
            if seen_cands.insert(id) {
                ordered_cand_ids.push(id);
            }
        }
        for (i, &id) in meta_ids.iter().enumerate() {
            meta_pos_map.insert(id, i + 1);
            if seen_cands.insert(id) {
                ordered_cand_ids.push(id);
            }
        }
        for (i, &(id, cos)) in sem_hits.iter().enumerate() {
            sem_pos_map.insert(id, (i + 1, cos));
            if seen_cands.insert(id) {
                ordered_cand_ids.push(id);
            }
        }

        let mut summary = Vec::new();
        if let Some(ref smart) = parsed.natural {
            if !smart.inferred.is_empty() {
                summary.push(format!("Excluded: {}", smart.inferred.join(", ")));
            }
        }

        let mut fuzzy_corrections: Vec<String> = corrections
            .iter()
            .map(|(word, values)| format!("{word} → {}*", values[0]))
            .collect();
        fuzzy_corrections.sort_unstable();

        if ordered_cand_ids.is_empty() {
            if !fuzzy_corrections.is_empty() {
                summary.push(format!("Typo: {}", fuzzy_corrections.join(", ")));
            }
            return Ok(ContentSearchResult {
                hits: Vec::new(),
                summary: summary.join(" · "),
                fuzzy_corrections,
                used_relaxed_match: false,
                facet_chips,
            });
        }

        // Hydrate candidate rows once
        let cand_ids_json = serde_json::to_string(&ordered_cand_ids)?;
        let hydrated = hydrate_candidates(&db, &cand_ids_json)?;
        let mut candidates: Vec<Candidate> = hydrated
            .into_iter()
            .map(|row| {
                let id = row.file_id;
                Candidate {
                    row,
                    body_rank: body_pos_map.get(&id).copied(),
                    meta_rank: meta_pos_map.get(&id).copied(),
                    sem_rank: sem_pos_map.get(&id).copied(),
                    decompressed_body: None,
                    score: 0.0,
                    any_covered: 0,
                    match_reason: "Exact",
                    match_tone: "success",
                }
            })
            .collect();

        // Compute exact vs fuzzy concept group coverage
        let groups_slice: &[Node] = match parsed.natural.as_ref() {
            Some(smart) => &smart.groups,
            None => std::slice::from_ref(&root),
        };
        let total_groups = groups_slice.len().min(64);
        let empty_corrections = HashMap::new();

        let mut exact_body_masks = HashMap::<i64, u64>::new();
        let mut exact_meta_masks = HashMap::<i64, u64>::new();
        let mut any_body_masks = HashMap::<i64, u64>::new();
        let mut any_meta_masks = HashMap::<i64, u64>::new();

        if total_groups == 1 && corrections.is_empty() {
            for c in &candidates {
                if c.body_rank.is_some() {
                    exact_body_masks.insert(c.row.file_id, 1);
                    any_body_masks.insert(c.row.file_id, 1);
                }
                if c.meta_rank.is_some() {
                    exact_meta_masks.insert(c.row.file_id, 1);
                    any_meta_masks.insert(c.row.file_id, 1);
                }
            }
        } else {
            for (idx, group) in groups_slice.iter().enumerate().take(total_groups) {
                let bit = 1u64 << idx;
                let (exact_g, exact_comp) = group.compile(&empty_corrections, false);
                if !exact_comp {
                    for id in fts_subset_matches(&db, "content_fts", &exact_g, &cand_ids_json)? {
                        *exact_body_masks.entry(id).or_default() |= bit;
                        *any_body_masks.entry(id).or_default() |= bit;
                    }
                    for id in fts_subset_matches(&db, "content_meta_fts", &exact_g, &cand_ids_json)? {
                        *exact_meta_masks.entry(id).or_default() |= bit;
                        *any_meta_masks.entry(id).or_default() |= bit;
                    }
                }
                if !corrections.is_empty() {
                    let (fuzzy_g, fuzzy_comp) = group.compile(&corrections, false);
                    if !fuzzy_comp && fuzzy_g != exact_g {
                        for id in fts_subset_matches(&db, "content_fts", &fuzzy_g, &cand_ids_json)? {
                            *any_body_masks.entry(id).or_default() |= bit;
                        }
                        for id in
                            fts_subset_matches(&db, "content_meta_fts", &fuzzy_g, &cand_ids_json)?
                        {
                            *any_meta_masks.entry(id).or_default() |= bit;
                        }
                    }
                }
            }
        }

        // Build lexical group patterns (for Smart multi-group adjacency + filename coverage)
        let exact_patterns = build_group_patterns(&groups_slice[..total_groups], &empty_corrections);
        let any_patterns = if corrections.is_empty() {
            exact_patterns.clone()
        } else {
            build_group_patterns(&groups_slice[..total_groups], &corrections)
        };

        let multi_group_smart = parsed.natural.is_some() && total_groups > 1;
        let mut literal_coverage = HashMap::<i64, usize>::new();
        if let Some(smart) = &parsed.natural {
            for term in &smart.expanded_concepts {
                let expr = quote(term, false);
                let mut matches = fts_subset_matches(&db, "content_fts", &expr, &cand_ids_json)?;
                matches.extend(fts_subset_matches(&db, "content_meta_fts", &expr, &cand_ids_json)?);
                for id in matches.into_iter().collect::<HashSet<_>>() {
                    *literal_coverage.entry(id).or_default() += 1;
                }
            }
        }

        for cand in &mut candidates {
            let id = cand.row.file_id;
            let rrf_body = cand.body_rank.map_or(0.0, |pos| 0.55 / (RRF_K + pos as f64));
            let rrf_meta = cand.meta_rank.map_or(0.0, |pos| 0.35 / (RRF_K + pos as f64));
            let rrf_sem = cand
                .sem_rank
                .map_or(0.0, |(pos, cos)| 0.40 / (RRF_K + pos as f64) * f64::from(cos));

            let mut exact_b = exact_body_masks.get(&id).copied().unwrap_or(0);
            let mut exact_m = exact_meta_masks.get(&id).copied().unwrap_or(0);
            let mut any_b = any_body_masks.get(&id).copied().unwrap_or(0);
            let mut any_m = any_meta_masks.get(&id).copied().unwrap_or(0);

            let meta_composite = format!(
                "{} {} {}",
                tokenize_meta_text(&cand.row.name),
                tokenize_meta_text(&cand.row.rel),
                tokenize_tags_text(&cand.row.tags)
            );
            let (exact_name_mask, name_adj_exact) =
                lexical_signals(&cand.row.name, &exact_patterns);
            let (any_name_mask, name_adj_any) = if corrections.is_empty() {
                (exact_name_mask, name_adj_exact)
            } else {
                lexical_signals(&cand.row.name, &any_patterns)
            };
            let (exact_meta_lex, _) = lexical_signals(&meta_composite, &exact_patterns);
            let (any_meta_lex, _) = if corrections.is_empty() {
                (exact_meta_lex, 0)
            } else {
                lexical_signals(&meta_composite, &any_patterns)
            };
            exact_m |= exact_meta_lex;
            any_m |= any_meta_lex;

            let mut body_adj = 0u32;
            if multi_group_smart {
                // Single-pass Brotli decompression cached on `cand.decompressed_body`
                if cand.decompressed_body.is_none() {
                    if let Some(compressed) = cand.row.compressed_body.take() {
                        cand.decompressed_body = decompress(&compressed).ok();
                    }
                }
                if let Some(ref body_str) = cand.decompressed_body {
                    let (eb_lex, _) = lexical_signals(body_str, &exact_patterns);
                    let (ab_lex, adj) = if corrections.is_empty() {
                        let (_, adj_e) = lexical_signals(body_str, &exact_patterns);
                        (eb_lex, adj_e)
                    } else {
                        lexical_signals(body_str, &any_patterns)
                    };
                    exact_b |= eb_lex;
                    any_b |= ab_lex;
                    body_adj = adj;
                }
            }

            let exact_union = exact_b | exact_m;
            let any_union = any_b | any_m;
            let exact_covered = exact_union.count_ones() as usize;
            let any_covered = any_union.count_ones() as usize;
            cand.any_covered = any_covered;

            let denom = total_groups.max(1) as f64;
            let coverage_bonus = if parsed.natural.is_some() {
                let ratio = (any_covered as f64) / denom;
                let full_bonus = if any_covered == total_groups && total_groups > 0 {
                    0.10
                } else {
                    0.0
                };
                0.10 * ratio + full_bonus
            } else {
                0.0
            };
            let exact_bonus = 0.002 * ((exact_covered as f64) / denom);
            let adj_bonus = f64::from(name_adj_any + body_adj) * 0.015;
            let filename_bonus = f64::from(any_name_mask.count_ones()) * 0.004;

            let literal_bonus = 0.015 * literal_coverage.get(&id).copied().unwrap_or(0) as f64 / denom;
            cand.score = rrf_body + rrf_meta + rrf_sem + coverage_bonus + exact_bonus
                + adj_bonus + filename_bonus + literal_bonus;

            // Classify human-readable (match_reason, match_tone)
            let lexical_absent = cand.body_rank.is_none() && cand.meta_rank.is_none() && any_covered == 0;
            let semantic_dominant = cand.sem_rank.is_some()
                && any_covered < total_groups
                && rrf_sem > (rrf_body + rrf_meta) * 1.25;

            let (reason, tone) = if lexical_absent || semantic_dominant {
                ("Concept", "imported")
            } else if any_covered == total_groups {
                if exact_covered < total_groups {
                    ("Fuzzy", "warning")
                } else if (cand.body_rank.is_none() && cand.meta_rank.is_some())
                    || (exact_b == 0 && exact_m != 0)
                    || (exact_m.count_ones() > exact_b.count_ones() && rrf_meta > rrf_body)
                {
                    ("Filename", "accent")
                } else {
                    ("Exact", "success")
                }
            } else if any_covered > exact_covered && exact_covered == 0 {
                ("Fuzzy", "warning")
            } else {
                ("Close", "warning")
            };
            cand.match_reason = reason;
            cand.match_tone = tone;
        }

        candidates.sort_by(|a, b| {
            b.score
                .total_cmp(&a.score)
                .then_with(|| a.row.file_id.cmp(&b.row.file_id))
        });
        candidates.truncate(limit);

        let mut used_relaxed_match = false;
        if parsed.natural.is_some() && total_groups > 1 && !candidates.is_empty() {
            let exact_count = candidates
                .iter()
                .filter(|c| c.any_covered == total_groups)
                .count();
            let close_count = candidates.len() - exact_count;
            used_relaxed_match = close_count > 0;
            if used_relaxed_match {
                summary.push(format!("Smart match ({exact_count} exact, {close_count} close)"));
            }
        }

        if !fuzzy_corrections.is_empty() {
            summary.push(format!("Typo: {}", fuzzy_corrections.join(", ")));
        }

        let highlight_terms: Vec<String> = positive
            .into_iter()
            .flat_map(|term| words(&term.text))
            .flat_map(|word| {
                let lc = word.to_lowercase();
                let extras = corrections.get(&lc).cloned().unwrap_or_default();
                std::iter::once(lc).chain(extras)
            })
            .collect();

        // Reuse already-decompressed body (or decompress at most once here if 1-group query)
        let hits = candidates
            .into_iter()
            .map(|mut cand| {
                let body_opt = match cand.decompressed_body.take() {
                    Some(text) => Some(text),
                    None => cand
                        .row
                        .compressed_body
                        .as_deref()
                        .and_then(|compressed| decompress(compressed).ok()),
                };
                let snippet = body_opt
                    .map(|body| {
                        let mark = options.mode == ContentMatchMode::Smart
                            && cand.match_reason != "Fuzzy";
                        crate::fuzzy::extract_snippet(&body, &highlight_terms, 240, mark).text
                    })
                    .unwrap_or_default();
                ContentSearchHit {
                    file_id: cand.row.file_id,
                    volume: cand.row.volume,
                    rel: cand.row.rel,
                    name: cand.row.name,
                    full_path: cand.row.full_path,
                    volume_label: cand.row.volume_label,
                    online: cand.row.online,
                    bytes: cand.row.bytes,
                    modified: cand.row.modified,
                    snippet,
                    rank: cand.score,
                    match_reason: cand.match_reason.into(),
                    match_tone: cand.match_tone.into(),
                }
            })
            .collect();

        Ok(ContentSearchResult {
            hits,
            summary: summary.join(" · "),
            fuzzy_corrections,
            used_relaxed_match,
            facet_chips,
        })
    }

    pub fn export_content(&self, destination: &Path, volume: Option<&str>) -> EngineResult<()> {
        self.export_with_cancel(destination, volume, &AtomicBool::new(false), &HashMap::new())
    }

    pub fn export_content_with_cancel(
        &self,
        destination: &Path,
        volume: Option<&str>,
        cancel: &AtomicBool,
    ) -> EngineResult<()> {
        self.export_with_cancel(destination, volume, cancel, &HashMap::new())
    }

    pub(crate) fn export_with_cancel(
        &self,
        destination: &Path,
        volume: Option<&str>,
        cancel: &AtomicBool,
        aliases: &HashMap<String, String>,
    ) -> EngineResult<()> {
        let db = self.open()?;
        db.execute_batch("CREATE VIRTUAL TABLE temp.content_export_terms USING fts5vocab(main,content_fts,instance); BEGIN;")?;
        let mut statement = db.prepare(
            "SELECT c.file_id,volume,rel,bytes,modified,extractor_ver,status,indexed_at,
              brotli_text,text_bytes,text_cap FROM content_files c JOIN content_sizes s ON s.file_id=c.file_id
              WHERE (?1 IS NULL OR volume=?1) ORDER BY c.file_id",
        )?;
        let mut records = statement.query([volume])?;
        atomic_write(destination, |file| -> EngineResult<()> {
            crate::sync::write_brotli(file, |mut output| {
                output.write_all(b"FICONTENT1")?;
                while let Some(row) = records.next()? {
                    check_cancel(cancel)?;
                    let id: i64 = row.get(0)?;
                    let record = Record {
                        volume: row.get(1)?,
                        rel: row.get(2)?,
                        bytes: row.get(3)?,
                        modified: row.get(4)?,
                        version: row.get(5)?,
                        status: row.get(6)?,
                        indexed_at: row.get(7)?,
                        snippet: row.get(8)?,
                        text_bytes: row.get(9)?,
                        text_cap: row.get(10)?,
                    };
                    let terms = if record.status == 1 && record.snippet.is_none() {
                        let terms: Vec<String> = rows(
                            &db,
                            "SELECT term FROM content_export_terms WHERE doc=?1 ORDER BY offset",
                            [id],
                            |r| r.get(0),
                        )?;
                        Some(terms.join(" "))
                    } else {
                        None
                    };
                    // Archives sent through cloud sync are also usable with a remote offline inventory.
                    for volume in std::iter::once(&record.volume).chain(aliases.get(&record.volume))
                    {
                        output.write_all(&[1])?;
                        write_field(&mut output, volume.as_bytes())?;
                        write_field(&mut output, record.rel.as_bytes())?;
                        output.write_all(&record.bytes.to_le_bytes())?;
                        output.write_all(&record.modified.to_le_bytes())?;
                        output.write_all(&record.version.to_le_bytes())?;
                        output.write_all(&record.status.to_le_bytes())?;
                        output.write_all(&record.indexed_at.to_le_bytes())?;
                        output.write_all(&record.text_bytes.to_le_bytes())?;
                        output.write_all(&record.text_cap.to_le_bytes())?;
                        output.write_all(&[u8::from(record.snippet.is_some())])?;
                        if let Some(snippet) = &record.snippet {
                            write_field(&mut output, snippet)?;
                        } else if let Some(terms) = &terms {
                            write_field(&mut output, terms.as_bytes())?;
                        }
                    }
                }
                check_cancel(cancel)?;
                output.write_all(&[0])?;
                Ok(())
            })
        })
    }

    pub fn import_content(&self, source: &Path) -> EngineResult<usize> {
        self.import_with_cancel(source, &AtomicBool::new(false))
    }

    pub fn import_content_with_cancel(
        &self,
        source: &Path,
        cancel: &AtomicBool,
    ) -> EngineResult<usize> {
        self.import_with_cancel(source, cancel)
    }

    pub(crate) fn import_with_cancel(
        &self,
        source: &Path,
        cancel: &AtomicBool,
    ) -> EngineResult<usize> {
        let mut reader = brotli::Decompressor::new(File::open(source)?, 65536);
        if &read_fixed::<10>(&mut reader)? != b"FICONTENT1" {
            return Err(invalid("Unrecognized content archive").into());
        }
        let mut db = self.attached()?;
        ensure_meta_fts_backfill(&mut db)?;
        let active_pack = crate::semantic::load_pack_setting(&self.catalog.data_dir);
        let active_embedder = if active_pack.is_enabled() {
            crate::semantic::load_embedder(&self.catalog.data_dir, active_pack).ok()
        } else {
            None
        };
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let mut imported = 0;
        while read_bool(&mut reader)? {
            check_cancel(cancel)?;
            let record = Record {
                volume: String::from_utf8(read_field(&mut reader)?)?,
                rel: String::from_utf8(read_field(&mut reader)?)?,
                bytes: i64::from_le_bytes(read_fixed(&mut reader)?),
                modified: i64::from_le_bytes(read_fixed(&mut reader)?),
                version: i32::from_le_bytes(read_fixed(&mut reader)?),
                status: i32::from_le_bytes(read_fixed(&mut reader)?),
                indexed_at: i64::from_le_bytes(read_fixed(&mut reader)?),
                text_bytes: i32::from_le_bytes(read_fixed(&mut reader)?),
                text_cap: i32::from_le_bytes(read_fixed(&mut reader)?),
                snippet: if read_bool(&mut reader)? {
                    Some(read_field(&mut reader)?)
                } else {
                    None
                },
            };
            if !safe_relative(&record.rel)
                || record.bytes < 0
                || !(0..=MAX_FIELD as i32).contains(&record.text_bytes)
                || record.text_cap <= 0
                || !matches!(record.status, 1 | 2)
                || (record.status == 2 && record.snippet.is_some())
            {
                return Err(invalid("Invalid content archive record").into());
            }
            let body = if let Some(ref snippet) = record.snippet {
                decompress(snippet)?
            } else if record.status == 1 {
                String::from_utf8(read_field(&mut reader)?)?
            } else {
                String::new()
            };
            if let Some(id) = lookup(&tx, &record)? {
                let raw_name = write(&tx, id, &record, &body)?;
                if record.status == 1 && record.snippet.is_some() {
                    if let Some(ref emb) = active_embedder {
                        if let Some(vec) =
                            crate::semantic::encode_document(emb, &raw_name, &record.rel, &body)
                        {
                            crate::semantic::upsert_vector(
                                &tx,
                                id,
                                active_pack,
                                &vec,
                                record.indexed_at,
                            )?;
                        }
                    }
                }
                imported += 1;
            }
        }
        if reader.read(&mut [0])? != 0 {
            return Err(invalid("Unexpected content archive data").into());
        }
        if imported > 0 {
            tx.execute(
                "INSERT OR REPLACE INTO content_meta(key, value) VALUES('meta_fts_v1', '1')",
                [],
            )?;
        }
        check_cancel(cancel)?;
        tx.commit()?;
        if imported > 0 {
            optimize(&db)?;
        }
        Ok(imported)
    }
}

pub(crate) fn tokenize_meta_text(s: &str) -> String {
    s.chars()
        .map(|c| if matches!(c, '\\' | '/' | '_') { ' ' } else { c })
        .collect()
}

pub(crate) fn tokenize_tags_text(s: &str) -> String {
    s.chars()
        .map(|c| if matches!(c, ',' | '_' | '/' | '\\' | '#') { ' ' } else { c })
        .collect()
}

fn ensure_meta_fts_backfill(db: &mut Connection) -> rusqlite::Result<()> {
    let done: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM content_meta WHERE key = 'meta_fts_v1')",
        [],
        |r| r.get(0),
    )?;
    if done {
        return Ok(());
    }
    let has_indexed: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM content_files WHERE status = 1)",
        [],
        |r| r.get(0),
    )?;
    if !has_indexed {
        return Ok(());
    }
    let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let already_done: bool = tx.query_row(
        "SELECT EXISTS(SELECT 1 FROM content_meta WHERE key = 'meta_fts_v1')",
        [],
        |r| r.get(0),
    )?;
    if !already_done {
        tx.execute_batch(
            "DROP TABLE IF EXISTS main.content_meta_fts;
             CREATE VIRTUAL TABLE main.content_meta_fts
             USING fts5(name, path, tags, content='', contentless_delete=1, tokenize='porter unicode61 remove_diacritics 2');",
        )?;
        tx.execute(
            "INSERT INTO main.content_meta_fts(rowid, name, path, tags)
             SELECT c.file_id,
                    replace(replace(replace(f.name, char(92), ' '), '/', ' '), '_', ' '),
                    replace(replace(replace(c.rel, char(92), ' '), '/', ' '), '_', ' '),
                    replace(replace(replace(replace(replace(coalesce(a.tags, ''), ',', ' '), '_', ' '), '/', ' '), char(92), ' '), '#', ' ')
             FROM main.content_files c
             JOIN source_catalog.files f ON f.id = c.file_id
             LEFT JOIN source_catalog.file_annotations a ON a.volume = f.volume AND a.rel = f.rel
             WHERE c.status = 1 AND f.removed IS NULL",
            [],
        )?;
        tx.execute(
            "INSERT OR REPLACE INTO main.content_meta(key, value) VALUES('meta_fts_v1', '1')",
            [],
        )?;
    }
    tx.commit()
}

fn lookup(db: &Connection, r: &Record) -> rusqlite::Result<Option<i64>> {
    db.query_row(
        "SELECT f.id FROM source_catalog.files f
        WHERE (f.volume,f.rel,f.bytes,f.modified)=(?1,?2,?3,?4) AND f.removed IS NULL AND (f.attributes & 16)=0
        AND NOT EXISTS(SELECT 1 FROM content_files c WHERE c.file_id=f.id AND
         (c.volume,c.rel,c.bytes,c.modified)=(f.volume,f.rel,f.bytes,f.modified)
         AND (c.extractor_ver,c.indexed_at)>=(?5,?6))",
        params![r.volume, r.rel, r.bytes, r.modified, r.version, r.indexed_at],
        |r| r.get(0),
    )
    .optional()
}

fn write(db: &Connection, id: i64, r: &Record, body: &str) -> rusqlite::Result<String> {
    db.execute("DELETE FROM content_fts WHERE rowid=?1", [id])?;
    db.execute("DELETE FROM content_meta_fts WHERE rowid=?1", [id])?;
    let raw_name = if r.status == 1 {
        let (raw_name, raw_tags): (String, String) = db
            .query_row(
                "SELECT f.name, coalesce(a.tags, '')
                 FROM source_catalog.files f
                 LEFT JOIN source_catalog.file_annotations a
                   ON a.volume = f.volume AND a.rel = f.rel
                 WHERE f.id = ?1",
                [id],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()?
            .unwrap_or_else(|| {
                let fallback = r
                    .rel
                    .rsplit(['\\', '/'])
                    .next()
                    .unwrap_or(&r.rel)
                    .to_owned();
                (fallback, String::new())
            });
        let clean_name = tokenize_meta_text(&raw_name);
        let clean_path = tokenize_meta_text(&r.rel);
        let clean_tags = tokenize_tags_text(&raw_tags);
        db.execute(
            "INSERT INTO content_fts(rowid, body) VALUES(?1, ?2)",
            params![id, body],
        )?;
        db.execute(
            "INSERT INTO content_meta_fts(rowid, name, path, tags) VALUES(?1, ?2, ?3, ?4)",
            params![id, clean_name, clean_path, clean_tags],
        )?;
        raw_name
    } else {
        String::new()
    };
    db.execute(
        "INSERT OR REPLACE INTO content_files VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)",
        params![
            id,
            r.volume,
            r.rel,
            r.bytes,
            r.modified,
            r.version,
            r.status,
            r.indexed_at,
            r.snippet
        ],
    )?;
    db.execute(
        "INSERT OR REPLACE INTO content_sizes VALUES(?1,?2,?3)",
        params![id, r.text_bytes, r.text_cap],
    )?;
    Ok(raw_name)
}

fn optimize(db: &Connection) -> EngineResult<()> {
    db.execute_batch(
        "INSERT INTO main.content_fts(content_fts) VALUES('optimize');
         INSERT INTO main.content_meta_fts(content_meta_fts) VALUES('optimize');
         PRAGMA main.incremental_vacuum; PRAGMA main.optimize;",
    )?;
    Ok(())
}

fn extract_required_exclusions(node: &Node, out: &mut Vec<Node>) {
    match node {
        Node::Not(inner) => out.push((**inner).clone()),
        Node::Join(a, b, false) => {
            extract_required_exclusions(a, out);
            extract_required_exclusions(b, out);
        }
        _ => {}
    }
}

fn build_semantic_query_text(
    cleaned_query: &str,
    natural: Option<&content_query::SmartQuery>,
) -> String {
    let Some(smart) = natural else {
        return cleaned_query.to_owned();
    };
    if smart.excluded.is_empty() {
        return cleaned_query.to_owned();
    }
    let mut words_out = Vec::new();
    for group in &smart.groups {
        let mut terms = Vec::new();
        group.positive(false, &mut terms);
        for term in terms {
            if !term.verbatim || term.phrase {
                // Keep user-typed terms (non-verbatim or explicit quoted phrases), skip synthetic verbatim single-word synonyms if desired
                if !term.verbatim {
                    words_out.push(term.text.as_str());
                } else if cleaned_query.to_lowercase().contains(&term.text) {
                    words_out.push(term.text.as_str());
                }
            }
        }
    }
    if words_out.is_empty() {
        cleaned_query.to_owned()
    } else {
        words_out.join(" ")
    }
}

fn query_facet_only(
    db: &Connection,
    args: &SqlFacetArgs<'_>,
    limit: usize,
) -> rusqlite::Result<Vec<HydratedRow>> {
    let sql = format!(
        "SELECT f.id, f.volume, f.rel, f.name, v.mount, v.label, v.online, f.bytes, f.modified, c.brotli_text
         FROM content_files c
         JOIN source_catalog.files f
           ON f.id = c.file_id AND (f.volume, f.rel, f.bytes, f.modified) = (c.volume, c.rel, c.bytes, c.modified)
         JOIN source_catalog.volumes v ON v.id = f.volume
         WHERE c.status = 1 AND f.removed IS NULL AND (f.attributes & 16) = 0
           {FACET_WHERE_SQL}
         ORDER BY f.modified DESC, f.id ASC
         LIMIT $limit"
    );
    rows(
        db,
        &sql,
        named_params! {
            "$volume": args.volume,
            "$ext": args.explicit_ext,
            "$facet_exts": args.facet_exts_json,
            "$min_bytes": args.min_bytes,
            "$max_bytes": args.max_bytes,
            "$mod_after": args.mod_after,
            "$mod_before": args.mod_before,
            "$online": args.online,
            "$tags_json": args.tags_json,
            "$excl_ids": args.excl_ids_json,
            "$limit": sql_limit(limit),
        },
        |r| {
            let rel: String = r.get(2)?;
            let mount: String = r.get(4)?;
            Ok(HydratedRow {
                file_id: r.get(0)?,
                volume: r.get(1)?,
                full_path: local_path(&mount, &rel),
                rel,
                name: r.get(3)?,
                volume_label: r.get(5)?,
                online: r.get(6)?,
                bytes: r.get(7)?,
                modified: r.get(8)?,
                tags: String::new(),
                compressed_body: r.get(9)?,
            })
        },
    )
}

fn query_fts_ranked(
    db: &Connection,
    table: &str,
    bm25_expr: &str,
    expression: &str,
    complement: bool,
    args: &SqlFacetArgs<'_>,
    limit: usize,
) -> rusqlite::Result<Vec<i64>> {
    let sql = if complement {
        format!(
            "SELECT c.file_id
             FROM content_files c
             JOIN source_catalog.files f
               ON f.id = c.file_id AND (f.volume, f.rel, f.bytes, f.modified) = (c.volume, c.rel, c.bytes, c.modified)
             JOIN source_catalog.volumes v ON v.id = f.volume
             WHERE c.status = 1
               AND c.file_id NOT IN (SELECT rowid FROM {table} WHERE {table} MATCH $query)
               AND f.removed IS NULL AND (f.attributes & 16) = 0
               {FACET_WHERE_SQL}
             ORDER BY c.file_id
             LIMIT $limit"
        )
    } else {
        format!(
            "SELECT c.file_id
             FROM {table}
             JOIN content_files c ON c.file_id = {table}.rowid
             JOIN source_catalog.files f
               ON f.id = c.file_id AND (f.volume, f.rel, f.bytes, f.modified) = (c.volume, c.rel, c.bytes, c.modified)
             JOIN source_catalog.volumes v ON v.id = f.volume
             WHERE {table} MATCH $query
               AND c.status = 1 AND f.removed IS NULL AND (f.attributes & 16) = 0
               {FACET_WHERE_SQL}
             ORDER BY {bm25_expr}, c.file_id
             LIMIT $limit"
        )
    };
    rows(
        db,
        &sql,
        named_params! {
            "$query": expression,
            "$volume": args.volume,
            "$ext": args.explicit_ext,
            "$facet_exts": args.facet_exts_json,
            "$min_bytes": args.min_bytes,
            "$max_bytes": args.max_bytes,
            "$mod_after": args.mod_after,
            "$mod_before": args.mod_before,
            "$online": args.online,
            "$tags_json": args.tags_json,
            "$excl_ids": args.excl_ids_json,
            "$limit": sql_limit(limit),
        },
        |r| r.get(0),
    )
}

fn filter_semantic_candidates(
    db: &Connection,
    raw_hits: &[(i64, f32)],
    args: &SqlFacetArgs<'_>,
) -> EngineResult<HashSet<i64>> {
    let ids: Vec<i64> = raw_hits.iter().map(|&(id, _)| id).collect();
    let cand_ids_json = serde_json::to_string(&ids)?;
    let sql = format!(
        "SELECT c.file_id
         FROM content_files c
         JOIN source_catalog.files f
           ON f.id = c.file_id AND (f.volume, f.rel, f.bytes, f.modified) = (c.volume, c.rel, c.bytes, c.modified)
         JOIN source_catalog.volumes v ON v.id = f.volume
         WHERE c.file_id IN (SELECT value FROM json_each($cand_ids))
           AND c.status = 1 AND f.removed IS NULL AND (f.attributes & 16) = 0
           {FACET_WHERE_SQL}"
    );
    let valid_vec: Vec<i64> = rows(
        db,
        &sql,
        named_params! {
            "$cand_ids": cand_ids_json,
            "$volume": args.volume,
            "$ext": args.explicit_ext,
            "$facet_exts": args.facet_exts_json,
            "$min_bytes": args.min_bytes,
            "$max_bytes": args.max_bytes,
            "$mod_after": args.mod_after,
            "$mod_before": args.mod_before,
            "$online": args.online,
            "$tags_json": args.tags_json,
            "$excl_ids": args.excl_ids_json,
        },
        |r| r.get(0),
    )?;
    Ok(valid_vec.into_iter().collect())
}

fn hydrate_candidates(db: &Connection, cand_ids_json: &str) -> rusqlite::Result<Vec<HydratedRow>> {
    rows(
        db,
        "SELECT f.id, f.volume, f.rel, f.name, v.mount, v.label, v.online, f.bytes, f.modified,
                coalesce(a.tags, ''), c.brotli_text
         FROM content_files c
         JOIN source_catalog.files f
           ON f.id = c.file_id AND (f.volume, f.rel, f.bytes, f.modified) = (c.volume, c.rel, c.bytes, c.modified)
         JOIN source_catalog.volumes v ON v.id = f.volume
         LEFT JOIN source_catalog.file_annotations a ON a.volume = f.volume AND a.rel = f.rel
         WHERE c.file_id IN (SELECT value FROM json_each(?1))
           AND c.status = 1 AND f.removed IS NULL AND (f.attributes & 16) = 0",
        [cand_ids_json],
        |r| {
            let rel: String = r.get(2)?;
            let mount: String = r.get(4)?;
            Ok(HydratedRow {
                file_id: r.get(0)?,
                volume: r.get(1)?,
                full_path: local_path(&mount, &rel),
                rel,
                name: r.get(3)?,
                volume_label: r.get(5)?,
                online: r.get(6)?,
                bytes: r.get(7)?,
                modified: r.get(8)?,
                tags: r.get(9)?,
                compressed_body: r.get(10)?,
            })
        },
    )
}

fn fts_subset_matches(
    db: &Connection,
    table: &str,
    expr: &str,
    cand_ids_json: &str,
) -> rusqlite::Result<Vec<i64>> {
    let sql = format!(
        "SELECT rowid FROM {table} WHERE {table} MATCH ?1 AND rowid IN (SELECT value FROM json_each(?2))"
    );
    rows(db, &sql, params![expr, cand_ids_json], |r| r.get(0))
}

fn build_group_patterns(
    groups: &[Node],
    corrections: &HashMap<String, Vec<String>>,
) -> Vec<Vec<(Vec<String>, bool)>> {
    groups
        .iter()
        .map(|group| {
            let mut terms = Vec::new();
            group.positive(false, &mut terms);
            terms
                .into_iter()
                .flat_map(|term| {
                    let exact_words: Vec<String> = words(&term.text)
                        .map(crate::fuzzy::fold_diacritics)
                        .filter(|w| !w.is_empty())
                        .collect();
                    let exact = (exact_words, term.prefix);
                    let lc_key = term.text.to_lowercase();
                    let fuzzy = corrections
                        .get(&lc_key)
                        .into_iter()
                        .flatten()
                        .filter(|_| !term.prefix && !term.phrase && !term.verbatim)
                        .map(|word| (vec![crate::fuzzy::fold_diacritics(word)], true));
                    std::iter::once(exact)
                        .chain(fuzzy)
                        .filter(|(pat, _)| !pat.is_empty())
                        .collect::<Vec<_>>()
                })
                .collect::<Vec<_>>()
        })
        .collect()
}

fn lexical_signals(text: &str, groups: &[Vec<(Vec<String>, bool)>]) -> (u64, u32) {
    let folded = crate::fuzzy::fold_diacritics(text);
    let tokens = words(&folded).collect::<Vec<_>>();
    if tokens.is_empty() || groups.is_empty() {
        return (0, 0);
    }
    let mut covered = 0u64;
    let mut adjacency = 0u32;
    let mut previous_ends = vec![false; tokens.len() + 1];
    let mut ends = vec![false; tokens.len() + 1];
    for (index, group) in groups.iter().enumerate().take(64) {
        let mut adjacent = false;
        for (pattern, prefix) in group {
            if pattern.is_empty() || pattern.len() > tokens.len() {
                continue;
            }
            for (start, window) in tokens.windows(pattern.len()).enumerate() {
                let matched = window.iter().zip(pattern).enumerate().all(|(i, (token, word))| {
                    if *prefix && i + 1 == pattern.len() {
                        token.starts_with(word.as_str())
                    } else {
                        *token == word.as_str()
                    }
                });
                if matched {
                    covered |= 1u64 << index;
                    ends[start + pattern.len()] = true;
                    adjacent |= previous_ends[start];
                }
            }
        }
        adjacency += u32::from(adjacent);
        std::mem::swap(&mut ends, &mut previous_ends);
        ends.fill(false);
    }
    (covered, adjacency)
}

fn compress(input: &[u8]) -> io::Result<Vec<u8>> {
    let mut encoder = brotli::CompressorWriter::new(Vec::new(), 4096, 4, 21);
    encoder.write_all(input)?;
    Ok(encoder.into_inner())
}

fn decompress(input: &[u8]) -> io::Result<String> {
    let mut text = Vec::new();
    brotli::Decompressor::new(input, 4096)
        .take(MAX_FIELD as u64 + 1)
        .read_to_end(&mut text)?;
    if text.len() > MAX_FIELD {
        return Err(invalid("Content text exceeds limit"));
    }
    String::from_utf8(text).map_err(|error| invalid(error.to_string()))
}

fn write_field(output: &mut impl Write, data: &[u8]) -> io::Result<()> {
    if data.len() > MAX_FIELD {
        return Err(invalid("Content archive field exceeds limit"));
    }
    output.write_all(&(data.len() as i32).to_le_bytes())?;
    output.write_all(data)
}

fn read_field(input: &mut impl Read) -> io::Result<Vec<u8>> {
    let len = u32::from_le_bytes(read_fixed(input)?) as usize;
    if len > MAX_FIELD {
        return Err(invalid("Invalid content archive field length"));
    }
    let mut field = vec![0; len];
    input.read_exact(&mut field)?;
    Ok(field)
}

fn read_fixed<const N: usize>(input: &mut impl Read) -> io::Result<[u8; N]> {
    let mut result = [0; N];
    input.read_exact(&mut result)?;
    Ok(result)
}

fn read_bool(input: &mut impl Read) -> io::Result<bool> {
    match read_fixed::<1>(input)?[0] {
        0 => Ok(false),
        1 => Ok(true),
        _ => Err(invalid("Invalid content archive marker")),
    }
}

pub(crate) fn safe_relative(rel: &str) -> bool {
    !rel.is_empty()
        && rel.encode_utf16().count() <= 32767
        && rel.split('\\').all(|part| {
            !(part.is_empty()
                || part == "."
                || part == ".."
                || part.ends_with(['.', ' '])
                || part.chars().any(|c| c < ' ' || ":/<>\"|?*".contains(c)))
        })
}

fn noise_path(rel: &str) -> bool {
    let key = rel.to_ascii_lowercase();
    [
        "windows\\",
        "program files\\",
        "program files (x86)\\",
        "programdata\\",
        "$recycle.bin\\",
        "system volume information\\",
    ]
    .iter()
    .any(|prefix| key.starts_with(prefix))
        || [
            "\\node_modules\\",
            "\\.git\\",
            "\\appdata\\local\\temp\\",
            "\\appdata\\local\\packages\\",
            "\\obj\\",
            "\\bin\\",
        ]
        .iter()
        .any(|part| key.contains(part))
}

fn background_priority() {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::System::Threading::{
            GetCurrentThread, SetThreadPriority, THREAD_MODE_BACKGROUND_BEGIN,
        };
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN as i32);
    }
}
