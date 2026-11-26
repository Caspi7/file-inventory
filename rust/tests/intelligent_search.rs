use std::{
    fs::{self, File, FileTimes},
    io::Write,
    path::{Path, PathBuf},
    sync::{atomic::AtomicBool, Arc},
    time::{Duration, Instant, UNIX_EPOCH},
};

use chrono::Utc;
use file_inventory::{
    catalog::{Catalog, SearchFilter, Volume},
    content::{ContentIndex, ContentScanMode, ContentScanOptions, ContentSearchOptions, ContentSearchResult},
    scanner,
    semantic::{self, SemanticPack},
};
use rusqlite::Connection;
use serde_json::json;

struct Fixture {
    root: PathBuf,
    volume: Volume,
    index: ContentIndex,
}

impl Fixture {
    fn new() -> Self {
        let root = std::env::temp_dir().join(format!("intelligent-search-{:032x}", rand::random::<u128>()));
        let mount = root.join("drive");
        fs::create_dir_all(&mount).unwrap();
        let volume = Volume {
            id: "fixture".into(),
            label: "Fixture".into(),
            mount: mount.to_str().unwrap().to_owned(),
            online: true,
            ..Default::default()
        };
        let index = ContentIndex::new(Catalog::new(root.join("data")));
        Self { root, volume, index }
    }

    fn path(&self, name: &str) -> PathBuf {
        Path::new(&self.volume.mount).join(name)
    }

    fn write(&self, name: &str, body: &str) {
        let path = self.path(name);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, body).unwrap();
    }

    fn scan_catalog(&self) {
        let result = scanner::scan(&self.index.catalog, &self.volume, cancel(), |_| {}).unwrap();
        assert_eq!(result.outcome, "Complete");
    }

    fn scan_content(&self, expected: u64) {
        let options = ContentScanOptions { mode: ContentScanMode::Balanced, ..Default::default() };
        let progress = self.index.scan(&options, cancel(), |_| {}).unwrap();
        assert_eq!((progress.indexed, progress.failed), (expected, 0));
    }

    fn search(&self, query: &str) -> ContentSearchResult {
        self.index.search(query, None, None, 100, &ContentSearchOptions::default()).unwrap()
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

fn cancel() -> Arc<AtomicBool> {
    Arc::new(AtomicBool::new(false))
}

fn names(result: &ContentSearchResult) -> Vec<&str> {
    let mut names: Vec<_> = result.hits.iter().map(|hit| hit.name.as_str()).collect();
    names.sort_unstable();
    names
}

fn chip_kinds(result: &ContentSearchResult) -> Vec<&str> {
    let mut kinds: Vec<_> = result.facet_chips.iter().map(|chip| chip.kind.as_str()).collect();
    kinds.sort_unstable();
    kinds
}

fn write_pdf(path: &Path, body: &str) {
    let stream = format!("BT ({body}) Tj ET");
    fs::write(
        path,
        format!("%PDF-1.4\n1 0 obj\n<< /Length {} >>\nstream\n{stream}\nendstream\nendobj\n%%EOF\n", stream.len()),
    ).unwrap();
}

fn write_sheet(path: &Path, body: &str, large: bool) {
    let mut zip = zip::ZipWriter::new(File::create(path).unwrap());
    zip.start_file("xl/worksheets/sheet1.xml", zip::write::SimpleFileOptions::default()).unwrap();
    write!(zip, "<worksheet><row><c t=\"inlineStr\"><is><t>{body}</t></is></c></row></worksheet>").unwrap();
    if large {
        zip.start_file(
            "padding.bin",
            zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored),
        ).unwrap();
        zip.write_all(&vec![0; 10_000_000]).unwrap();
    }
    zip.finish().unwrap();
}

#[test]
fn natural_language_facets_and_exclusions_in_english_and_dutch() {
    let fixture = Fixture::new();
    let now = Utc::now();
    let within_month = now - chrono::Duration::days(29);
    let older = now - chrono::Duration::days(31);
    for (name, body, large, stamp) in [
        ("accepted.pdf", "invoice", true, within_month),
        ("excluded.pdf", "invoice draft", true, within_month),
        ("undersized.pdf", "invoice", false, within_month),
        ("stale.pdf", "invoice", true, older),
        ("wrong-type.txt", "invoice begroting", true, within_month),
        ("approved.xlsx", "begroting", true, within_month),
        ("revision.xlsx", "begroting concept", true, within_month),
        ("undersized.xlsx", "begroting", false, within_month),
        ("unrelated.xlsx", "protocol", true, within_month),
    ] {
        let path = fixture.path(name);
        match path.extension().unwrap().to_str().unwrap() {
            "pdf" => write_pdf(&path, body),
            "xlsx" => write_sheet(&path, body, large),
            _ => fixture.write(name, &format!("{body}{}", " ".repeat(4096))),
        }
        let file = File::options().write(true).open(&path).unwrap();
        if large && path.extension().unwrap() != "xlsx" {
            file.set_len(10_000_001).unwrap();
        }
        file.set_times(FileTimes::new().set_modified(
            UNIX_EPOCH + Duration::from_secs(stamp.timestamp() as u64),
        )).unwrap();
    }
    fixture.scan_catalog();
    fixture.scan_content(9);

    let english = fixture.search("large pdf invoice from last month without draft");
    assert_eq!(names(&english), ["accepted.pdf"]);
    assert_eq!(chip_kinds(&english), ["date", "ext", "size"]);
    assert!(english.summary.contains("Excluded: draft"));
    let dutch = fixture.search("grote excel begroting zonder concept");
    assert_eq!(names(&dutch), ["approved.xlsx"]);
    assert_eq!(chip_kinds(&dutch), ["ext", "size"]);
    assert!(dutch.summary.contains("Excluded: concept"));
    let facet_only = fixture.search("large pdfs");
    assert_eq!(names(&facet_only), ["accepted.pdf", "excluded.pdf", "stale.pdf"]);
    assert_eq!(chip_kinds(&facet_only), ["ext", "size"]);
}

#[test]
fn expands_bilingual_concept_synonyms() {
    let fixture = Fixture::new();
    for (file, body) in [
        ("one.txt", "invoice"),
        ("two.txt", "automobile"),
        ("three.txt", "vehicle"),
        ("other.txt", "telescope"),
    ] {
        fixture.write(file, body);
    }
    fixture.scan_catalog();
    fixture.scan_content(4);
    assert_eq!(names(&fixture.search("factuur")), ["one.txt"]);
    assert_eq!(names(&fixture.search("car")), ["three.txt", "two.txt"]);
}

#[test]
fn metadata_fts_retrieves_filename_path_and_tags_and_backfills_legacy_rows() {
    let fixture = Fixture::new();
    for name in ["2025_Accountant_Tax_Return.txt", "schedule.txt", "Archives/letter.txt"] {
        fixture.write(name, "See attached schedule.");
    }
    fixture.scan_catalog();
    // Add the tag after indexing to exercise live FTS maintenance as well as migration.
    fixture.scan_content(3);
    fixture.index.catalog.save_annotation("fixture", "schedule.txt", "#accounting", "").unwrap();

    let verify = || {
        for (query, file) in [
            ("tax return", "2025_Accountant_Tax_Return.txt"),
            ("accounting", "schedule.txt"),
            ("archives", "letter.txt"),
        ] {
            let result = fixture.search(query);
            let hit = result.hits.iter().find(|hit| hit.name == file)
                .unwrap_or_else(|| panic!("{query}: {:?}", names(&result)));
            assert_eq!(hit.match_reason, "Filename", "{query}");
            assert_eq!(hit.snippet, "See attached schedule.");
        }
    };
    verify();

    // Recreate the pre-v1.6 shape while keeping the existing indexed content.
    let db = Connection::open(fixture.index.database_path()).unwrap();
    db.execute_batch("DROP TABLE content_meta_fts; DELETE FROM content_meta WHERE key = 'meta_fts_v1';").unwrap();
    verify();
    let marker: String = db.query_row(
        "SELECT value FROM content_meta WHERE key = 'meta_fts_v1'", [], |row| row.get(0),
    ).unwrap();
    assert_eq!(marker, "1");
    let indexed: u64 = db.query_row("SELECT count(*) FROM content_meta_fts", [], |row| row.get(0)).unwrap();
    assert_eq!(indexed, 3);
}

#[test]
fn rare_literal_typo_still_expands_to_the_common_term() {
    let fixture = Fixture::new();
    fixture.write("noisy.txt", "invoce");
    for n in 0..25 {
        fixture.write(&format!("record-{n:02}.txt"), "invoice");
    }
    fixture.scan_catalog();
    fixture.scan_content(26);
    let result = fixture.search("invoce");
    assert!(result.fuzzy_corrections.iter().any(|correction| correction.ends_with("invoic*")), "{:?}", result.fuzzy_corrections);
    assert_eq!(result.hits.iter().filter(|hit| hit.name.starts_with("record-")).count(), 25);
    assert!(result.hits.iter().any(|hit| hit.name == "noisy.txt"));
}

#[test]
fn catalog_search_ranks_filename_ahead_of_folder_matches() {
    let fixture = Fixture::new();
    for name in [
        "Projects/Invoice/cover.pdf",
        "Projects/2025/invoice.pdf",
        "Projects Invoice.pdf",
        "Projects/2025/cover.pdf",
    ] {
        fixture.write(name, "");
    }
    fixture.scan_catalog();
    let filter = SearchFilter { query: "projects invoice".into(), extension: Some("pdf".into()), ..Default::default() };
    let hits = fixture.index.catalog.search(&filter, 10).unwrap();
    assert_eq!(hits.len(), 3);
    assert_eq!(hits[0].name, "Projects Invoice.pdf");
    assert_eq!(hits[1].rel, r"Projects\2025\invoice.pdf");
    assert_eq!(hits[2].rel, r"Projects\Invoice\cover.pdf");
}

fn enable_synthetic_pack(fixture: &Fixture) {
    let pack = SemanticPack::EnglishFast;
    let dir = fixture.index.catalog.data_dir.join("models").join("potion-base-8m");
    assert_eq!(semantic::pack_dir(&fixture.index.catalog.data_dir, pack).unwrap(), dir);
    fs::create_dir_all(&dir).unwrap();
    let terms = ["[UNK]", "purchase", "acquisition", "invoice", "invoce", "telescope"];
    let axes = [None, Some(0), Some(0), Some(0), Some(0), Some(1)];
    let mut vocab = serde_json::Map::new();
    let mut payload = Vec::new();
    for (id, (term, axis)) in terms.into_iter().zip(axes).enumerate() {
        vocab.insert(term.into(), json!(id));
        for column in 0..pack.dims() {
            let weight: f32 = if Some(column) == axis { 1.0 } else { 0.0 };
            payload.extend_from_slice(&weight.to_le_bytes());
        }
    }
    let tokenizer = json!({
        "normalizer": { "type": "BertNormalizer", "lowercase": true },
        "model": { "type": "WordPiece", "unk_token": "[UNK]", "vocab": vocab }
    });
    let mut header = serde_json::to_vec(&json!({
        "embeddings": {
            "dtype": "F32", "shape": [terms.len(), pack.dims()], "data_offsets": [0, payload.len()]
        }
    })).unwrap();
    header.resize(header.len().next_multiple_of(8), b' ');
    let mut model = File::create(dir.join("model.safetensors")).unwrap();
    model.write_all(&(header.len() as u64).to_le_bytes()).unwrap();
    model.write_all(&header).unwrap();
    model.write_all(&payload).unwrap();
    fs::write(dir.join("tokenizer.json"), serde_json::to_vec(&tokenizer).unwrap()).unwrap();
    semantic::save_pack_setting(&fixture.index.catalog.data_dir, pack).unwrap();
}

fn backfill_vectors(fixture: &Fixture, expected: u64) {
    let pack = SemanticPack::EnglishFast;
    assert_eq!(semantic::backfill_from_index(&fixture.index.catalog, pack, &cancel(), |_, _| {}).unwrap(), expected);
    let db = fixture.index.open().unwrap();
    assert_eq!(semantic::vector_count(&db, pack), expected);
    let int8_rows: u64 = db.query_row(
        "SELECT count(*) FROM content_vectors WHERE typeof(vec) = 'blob' AND length(vec) = dims AND dims = ?1",
        [pack.dims() as i64],
        |row| row.get(0),
    ).unwrap();
    assert_eq!(int8_rows, expected);
}

#[test]
fn model2vec_surfaces_a_document_with_zero_lexical_overlap() {
    let fixture = Fixture::new();
    fixture.write("match.txt", "acquisition");
    fixture.write("other.txt", "telescope");
    fixture.scan_catalog();
    fixture.scan_content(2);
    assert!(fixture.search("purchase").hits.is_empty());

    enable_synthetic_pack(&fixture);
    backfill_vectors(&fixture, 2);
    let result = fixture.search("purchase");
    assert_eq!(names(&result), ["match.txt"]);
    assert_eq!(result.hits[0].match_reason, "Semantic");
    assert!(result.fuzzy_corrections.is_empty());
}

#[test]
fn hybrid_search_latency_on_2000_documents() {
    const DOCUMENTS: u64 = 2_000;
    let fixture = Fixture::new();
    for n in 0..DOCUMENTS {
        let (name, body) = match n {
            0 => ("semantic.txt".to_owned(), "acquisition"),
            1 => ("noise.txt".to_owned(), "invoce"),
            2 => ("invoice.txt".to_owned(), "telescope"),
            3..=85 => (format!("record-{n:04}.txt"), "invoice telescope acquisition"),
            _ => (format!("record-{n:04}.txt"), "telescope"),
        };
        fixture.write(&name, body);
    }
    fixture.scan_catalog();
    fixture.scan_content(DOCUMENTS);
    enable_synthetic_pack(&fixture);
    backfill_vectors(&fixture, DOCUMENTS);

    let started = Instant::now();
    let cold = fixture.search("invoce");
    let cold_elapsed = started.elapsed();
    assert!(cold.fuzzy_corrections.iter().any(|correction| correction.ends_with("invoic*")), "{:?}", cold.fuzzy_corrections);
    assert!(cold.hits.iter().any(|hit| hit.name.starts_with("record-") && hit.match_reason == "Fuzzy"));
    assert!(cold.hits.iter().any(|hit| hit.name == "invoice.txt"));
    assert!(cold.hits.iter().any(|hit| hit.name == "semantic.txt" && hit.match_reason == "Semantic"));
    assert_eq!(fixture.search("purchase").hits[0].match_reason, "Semantic");

    println!("hybrid {DOCUMENTS} docs, cold typo: {:.2} ms", cold_elapsed.as_secs_f64() * 1_000.0);
    for query in ["invoce", "invoice", "purchase"] {
        let mut samples = Vec::new();
        for _ in 0..21 {
            let started = Instant::now();
            let result = fixture.search(query);
            samples.push(started.elapsed());
            assert!(!result.hits.is_empty(), "{query}");
        }
        samples.sort_unstable();
        let ms = |duration: Duration| duration.as_secs_f64() * 1_000.0;
        println!(
            "hybrid {DOCUMENTS} docs, {query:?}: warm median {:.2} ms, p95 {:.2} ms, max {:.2} ms",
            ms(samples[10]), ms(samples[19]), ms(samples[20])
        );
        // A generous median budget tolerates scheduling noise on debug and CI runs.
        let budget = if cfg!(debug_assertions) { 750 } else { 250 };
        assert!(samples[10] < Duration::from_millis(budget), "{query}: {samples:?}");
    }
}
