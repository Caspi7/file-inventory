use std::{
    fs::{self,File,FileTimes}, io::{Read,Write}, path::{Path,PathBuf},
    sync::{Arc,atomic::{AtomicBool,Ordering}}, time::{UNIX_EPOCH,Duration},
};
use file_inventory::{
    catalog::{Catalog, SearchFilter, Volume, SavedSearch},
    content::{ContentIndex, ContentScanOptions, ContentSearchOptions, ContentMatchMode},
    duplicates::{find_duplicate_groups,verify_groups,DuplicateOptions},
    export::{self,InventoryExportFormat},
    scanner,
    sync::{CloudSyncConfig,decrypt_snapshot,encrypt_snapshot,sync_inventory,sync_content},
};
use rusqlite::{Connection,params};

struct Fixture(PathBuf);
impl Fixture {
    fn new()->Self {
        let path=std::env::temp_dir().join(format!("file-inventory-test-{:032x}",rand::random::<u128>()));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
    fn path(&self,name:&str)->PathBuf {self.0.join(name)}
    fn catalog(&self,name:&str)->Catalog {Catalog::new(self.path(name))}
    fn volume(&self,id:&str)->Volume {
        let mount=self.path(id);
        fs::create_dir_all(&mount).unwrap();
        Volume{id:id.into(),mount:mount.to_str().unwrap().to_owned(),label:id.into(),online:true,
            host:"FixturePC".into(),..Default::default()}
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        if self.0.starts_with(std::env::temp_dir()) {let _=fs::remove_dir_all(&self.0);}
    }
}
fn cancel()->Arc<AtomicBool> {Arc::new(AtomicBool::new(false))}
fn scan(catalog:&Catalog,volume:&Volume)->scanner::ScanOutcome {
    scanner::scan(catalog,volume,cancel(),|_|{}).unwrap()
}
fn search(catalog:&Catalog,query:&str)->Vec<String> {
    catalog.search(&SearchFilter{query:query.into(),..Default::default()},500).unwrap()
        .into_iter().map(|row|row.rel).collect()
}

#[test]
fn reference_schema_scan_incremental_search_and_storage_map() {
    let fixture=Fixture::new();
    let catalog=fixture.catalog("catalog");
    let volume=fixture.volume("drive");
    let root=Path::new(&volume.mount);
    fs::create_dir_all(root.join("Photos").join("Nested")).unwrap();
    fs::write(root.join("Photos").join("Annual Budget.txt"),b"1234567890").unwrap();
    fs::write(root.join("Photos").join("Old.txt"),b"1234").unwrap();
    fs::write(root.join("Photos").join("Nested").join("Budget draft.txt"),b"123456").unwrap();

    let reference=Connection::open_in_memory().unwrap();
    reference.execute_batch(include_str!("../../Catalog.sql")).unwrap();
    let db=catalog.open().unwrap();
    let objects=|db:&Connection| {
        db.prepare("SELECT type,name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY name").unwrap()
            .query_map([],|r|Ok((r.get::<_,String>(0)?,r.get::<_,String>(1)?,r.get::<_,Option<String>>(2)?)))
            .unwrap().collect::<rusqlite::Result<Vec<_>>>().unwrap()
    };
    assert_eq!(objects(&reference),objects(&db));
    assert_eq!(db.pragma_query_value(Some(rusqlite::DatabaseName::Main),"auto_vacuum",|r|r.get::<_,i64>(0)).unwrap(),2);
    assert_eq!(db.pragma_query_value(None,"journal_mode",|r|r.get::<_,String>(0)).unwrap(),"wal");
    drop(db);

    let first=scan(&catalog,&volume);
    assert_eq!(first.outcome,"Complete");
    assert_eq!((first.items,first.folders),(5,2));
    assert!(catalog.scan_changes(first.scan_id,100).unwrap().is_empty());
    assert_eq!(catalog.child_folders("drive","").unwrap()[0].has_children,true);
    assert_eq!(catalog.folder_stats_children("drive","").unwrap()[0].bytes,20);
    assert_eq!(search(&catalog,"bud ext:txt size:>8 -draft"),[r"Photos\Annual Budget.txt"]);
    assert_eq!(search(&catalog,"bu ext:txt").len(),2);
    let paged_filter = SearchFilter { query: "bu ext:txt".into(), ..Default::default() };
    let expected = catalog.search(&paged_filter, 2).unwrap();
    assert_eq!(catalog.search_page(&paged_filter, 1, 1).unwrap()[0].id, expected[1].id);
    let expected = catalog.browse_folder("drive", "photos", false, 2).unwrap();
    assert_eq!(catalog.browse_folder_page("drive", "photos", false, 1, 1).unwrap()[0].id, expected[1].id);
    assert_eq!(search(&catalog,"ph ext:txt").len(),0);
    assert_eq!(search(&catalog,"\"annual budget\" ext:.txt"),[r"Photos\Annual Budget.txt"]);
    assert!(catalog.search(&SearchFilter{query:"ext:\"txt".into(),..Default::default()},10).is_err());
    catalog.save_annotation("drive",r"Photos\Annual Budget.txt","Money, money, work","Private note").unwrap();
    assert_eq!(search(&catalog,"tag:money"),[r"Photos\Annual Budget.txt"]);
    assert_eq!(catalog.get_annotation("drive",r"Photos\Annual Budget.txt").unwrap().note,"Private note");

    fs::write(root.join("Photos").join("Annual Budget.txt"),b"larger replacement").unwrap();
    fs::remove_file(root.join("Photos").join("Old.txt")).unwrap();
    fs::write(root.join("Photos").join("New.txt"),b"abcd").unwrap();
    let second=scan(&catalog,&volume);
    let changes=catalog.scan_changes(second.scan_id,100).unwrap();
    assert!(changes.iter().any(|row|row.kind==1 && row.rel==r"Photos\New.txt"));
    assert!(changes.iter().any(|row|row.kind==2 && row.rel==r"Photos\Annual Budget.txt"));
    assert!(changes.iter().any(|row|row.kind==3 && row.rel==r"Photos\Old.txt"));
    assert!(changes.iter().filter(|row|row.kind==2 && row.rel!=r"Photos\Annual Budget.txt").all(|row|
        catalog.get_file("drive",&row.rel).unwrap().unwrap().is_directory()),"{changes:?}");
    assert_eq!(search(&catalog,"status:removed"),[r"Photos\Old.txt"]);
    assert_eq!(catalog.folder_stats_children("drive","").unwrap()[0].bytes,28);
    let history=catalog.scan_history(Some("drive"),10).unwrap();
    assert_eq!((history[0].added_count,history[0].modified_count,history[0].removed_count,history[0].bytes_delta),(1,changes.iter().filter(|row|row.kind==2).count() as i64,1,8));
    assert_eq!(catalog.browse_folder("drive","photos",false,100).unwrap().len(),3);
    catalog.update_mounted_volumes(&[]).unwrap();
    assert!(!catalog.volumes().unwrap()[0].online);
    assert_eq!(search(&catalog,"drive:drive online:false status:offline").len(),5);

    let db=catalog.open().unwrap();
    db.execute("INSERT INTO scans(volume,started,outcome) VALUES('drive',0,'Scanning')",[]).unwrap();
    db.execute("UPDATE volumes SET outcome='Scanning'",[]).unwrap();
    let orphan=catalog.data_dir.join("scan-00000000000000000000000000000001.sqlite3");
    fs::write(&orphan,b"orphaned staging database").unwrap();
    let session=catalog.start_session().unwrap();
    assert!(!orphan.exists());
    #[cfg(windows)]
    assert!(catalog.start_session().is_err());
    drop(session);
    assert_eq!(catalog.scan_history(None,1).unwrap()[0].outcome,"Interrupted");
    assert_eq!(catalog.volumes().unwrap()[0].outcome,"Interrupted");
}

#[test]
fn cancellation_preserves_the_catalog_and_move_identity() {
    let fixture=Fixture::new();
    let catalog=fixture.catalog("catalog");
    let volume=fixture.volume("drive");
    let root=Path::new(&volume.mount);
    fs::write(root.join("old.txt"),b"data").unwrap();
    scan(&catalog,&volume);
    let flag=cancel();
    assert!(scanner::scan(&catalog,&volume,flag.clone(),|_|flag.store(true,Ordering::Relaxed)).is_err());
    assert_eq!(catalog.scan_history(None,1).unwrap()[0].outcome,"Cancelled");
    assert_eq!(search(&catalog,"old"),["old.txt"]);
    fs::rename(root.join("old.txt"),root.join("new.txt")).unwrap();
    let second=scan(&catalog,&volume);
    let changes=catalog.scan_changes(second.scan_id,100).unwrap();
    if catalog.get_file("drive","new.txt").unwrap().unwrap().file_key.is_some() {
        assert_eq!(changes.len(),1);
        assert_eq!(changes[0].kind,4);
    } else {
        assert_eq!(changes.iter().map(|row|row.kind).collect::<Vec<_>>(),[1,3]);
    }
}

fn office_file(path:&Path,part:&str,xml:&str) {
    let mut zip=zip::ZipWriter::new(File::create(path).unwrap());
    zip.start_file(part,zip::write::SimpleFileOptions::default()).unwrap();
    zip.write_all(xml.as_bytes()).unwrap();
    zip.finish().unwrap();
}

fn pdf_file(path:&Path) {
    let mut z=flate2::write::ZlibEncoder::new(Vec::new(),flate2::Compression::default());
    z.write_all(br"BT [(helio) 10 (sphere)] TJ (quasar) Tj (escaped \(test\)) Tj ET").unwrap();
    let stream=z.finish().unwrap();
    let mut file=File::create(path).unwrap();
    write!(file,"%PDF-1.4\n1 0 obj\n<< /Length {} /Filter /FlateDecode >>\nstream\n",stream.len()).unwrap();
    file.write_all(&stream).unwrap();
    file.write_all(b"\nendstream\nendobj\n%%EOF\n").unwrap();
}

#[test]
fn content_formats_boolean_fuzzy_ranking_and_ficontent() {
    let fixture=Fixture::new();
    let catalog=fixture.catalog("catalog");
    let volume=fixture.volume("drive");
    let root=Path::new(&volume.mount);
    fs::write(root.join("statement.txt"),format!("{} invoice receipt alpha core quarterly ledger","filler ".repeat(100))).unwrap();
    fs::write(root.join("simple.txt"),"beta core archive quarterly").unwrap();
    fs::write(root.join("excluded.txt"),"alpha core excluded").unwrap();
    fs::write(root.join("wrong.txt"),"alpha unrelated").unwrap();
    fs::create_dir(root.join("Windows")).unwrap();
    fs::write(root.join("Windows").join("noise.txt"),"sentinel").unwrap();
    office_file(&root.join("meeting.docx"),"word/document.xml",
        r#"<w:document xmlns:w="urn:w"><w:body><w:p><w:r><w:t>minutes nomenclature</w:t></w:r></w:p></w:body></w:document>"#);
    office_file(&root.join("slides.pptx"),"ppt/slides/slide1.xml",
        r#"<p:sld xmlns:p="urn:p" xmlns:a="urn:a"><a:t>slideship</a:t></p:sld>"#);
    office_file(&root.join("sheet.xlsx"),"xl/worksheets/sheet1.xml",
        "<worksheet><row><c t=\"inlineStr\"><is><t>spreadsheet</t></is></c></row></worksheet>");
    for (ext,word) in [("odt","odfword"),("ods","odfsheet"),("odp","odfpresentation")] {
        office_file(&root.join(format!("office.{ext}")),"content.xml",
            &format!("<root><p>{word}</p></root>"));
    }
    pdf_file(&root.join("science.pdf"));
    scan(&catalog,&volume);
    let index=ContentIndex::new(catalog.clone());
    let options=ContentScanOptions::default();
    assert_eq!(index.estimate_scan(&options).unwrap().to_scan,11);
    let progress=index.scan(&options,cancel(),|_|{}).unwrap();
    assert_eq!((progress.indexed,progress.failed),(11,0));
    assert_eq!(index.estimate_scan(&options).unwrap().to_scan,0);
    let defaults=ContentSearchOptions::default();
    let strict=ContentSearchOptions {mode:ContentMatchMode::AllWords,fuzzy_matching:false};
    let query=|q:&str,options:&ContentSearchOptions|index.search(q,None,None,20,options).unwrap();
    for (term,name) in [
        ("nomenclature","meeting.docx"),("slideship","slides.pptx"),("spreadsheet","sheet.xlsx"),
        ("odfword","office.odt"),("odfsheet","office.ods"),("odfpresentation","office.odp"),
        ("heliosphere quasar","science.pdf"),
    ] {assert_eq!(query(term,&strict).hits[0].name,name);}
    assert!(query("sentinel",&strict).hits.is_empty());
    let hits=query("(alpha OR beta) AND core -excluded",&strict).hits;
    let mut names=hits.iter().map(|hit|hit.name.as_str()).collect::<Vec<_>>();
    names.sort_unstable();
    assert_eq!(names,["simple.txt","statement.txt"]);
    assert_eq!(query("\"invoice receipt\" AND NOT beta",&strict).hits[0].name,"statement.txt");
    assert_eq!(query("helio*",&strict).hits[0].name,"science.pdf");
    for typo in ["invoce","reciept","niovce"] {
        let result=query(typo,&defaults);
        assert_eq!(result.hits[0].name,"statement.txt","{typo}");
        assert!(result.hits[0].snippet.contains("invoice receipt"));
        assert!(!result.fuzzy_corrections.is_empty());
    }
    for malformed in ["(", "(alpha OR", "AND","\"unterminated", "NOT alpha", "alpha AND OR beta"] {
        assert!(query(malformed,&defaults).hits.is_empty(),"{malformed}");
    }
    let close=query("looking for the quarterly invoice ledger document",&defaults);
    assert!(close.used_relaxed_match);
    assert_eq!(close.hits[0].name,"statement.txt");
    index.optimize().unwrap();

    let archive=fixture.path("source.ficontent");
    index.export_content(&archive,None).unwrap();
    let mut magic=[0;10];
    brotli::Decompressor::new(File::open(&archive).unwrap(),4096).read_exact(&mut magic).unwrap();
    assert_eq!(&magic,b"FICONTENT1");
    let other=fixture.catalog("another-catalog");
    scan(&other,&volume);
    let imported=ContentIndex::new(other);
    assert_eq!(imported.import_content(&archive).unwrap(),11);
    assert_eq!(imported.import_content(&archive).unwrap(),0);
    let hit=&imported.search("nomenclature",None,None,20,&strict).unwrap().hits[0];
    assert!(hit.snippet.contains("minutes"));
    assert_eq!(imported.stats().unwrap().indexed_files,11);
    let options=ContentScanOptions {store_snippets:false,..options};
    index.scan(&options,cancel(),|_|{}).unwrap();
    index.export_content(&archive,None).unwrap();
    assert!(imported.import_content(&archive).unwrap()>0);
    let result=imported.search("invoice",None,None,20,&strict).unwrap();
    assert_eq!(result.hits[0].name,"statement.txt");
    assert!(result.hits[0].snippet.is_empty());
}

#[cfg(windows)]
#[test]
fn duplicate_verification_hash_cache_and_reports() {
    let fixture=Fixture::new();
    let catalog=fixture.catalog("catalog");
    let volume_a=fixture.volume("drive-a");
    let volume_b=fixture.volume("drive-b");
    let stamp=UNIX_EPOCH+Duration::from_secs(1_700_000_000);
    for (volume,name,body) in [(&volume_a,"=copy.txt",b"same"),(&volume_b,"other.txt",b"same"),(&volume_b,"unrelated.txt",b"diff")] {
        let path=Path::new(&volume.mount).join(name);
        fs::write(&path,body).unwrap();
        let file=File::options().write(true).open(path).unwrap();
        file.set_times(FileTimes::new().set_modified(stamp)).unwrap();
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::{Foundation::FILETIME,Storage::FileSystem::SetFileTime};
        let ticks=(1_700_000_000u64+11_644_473_600)*10_000_000;
        let time=FILETIME{dwLowDateTime:ticks as u32,dwHighDateTime:(ticks>>32) as u32};
        // Writing attributes through the fixture's file handle gives all candidates the same birth time.
        assert_ne!(unsafe {SetFileTime(file.as_raw_handle(),&time,std::ptr::null(),std::ptr::null())},0);
    }
    scan(&catalog,&volume_a);
    scan(&catalog,&volume_b);
    let groups=find_duplicate_groups(&catalog,&DuplicateOptions::default()).unwrap();
    assert_eq!(groups.len(),1);
    assert_eq!(groups[0].files.len(),3);
    let checked=verify_groups(&catalog,&groups,&cancel()).unwrap();
    assert_eq!(checked.len(),1);
    assert_eq!(checked[0].files.len(),2);
    assert_eq!(checked[0].full_hash.as_ref().unwrap().len(),64);
    assert_eq!(verify_groups(&catalog,&groups,&cancel()).unwrap()[0].full_hash,checked[0].full_hash);
    assert_eq!(catalog.open().unwrap().query_row("SELECT count(*) FROM file_hashes WHERE length(full_hash)=32",[],|row|row.get::<_,i64>(0)).unwrap(),3);

    let reports=[("report.html",InventoryExportFormat::Html),("report.csv",InventoryExportFormat::Csv),("report.json",InventoryExportFormat::Json)];
    for (name,format) in reports {
        export::write(&fixture.path(name),&groups[0].files,format,"Report <demo>",&cancel()).unwrap();
    }
    assert!(fs::read_to_string(fixture.path("report.html")).unwrap().contains("Report &lt;demo&gt;"));
    let csv=fs::read(fixture.path("report.csv")).unwrap();
    assert!(csv.starts_with(&[0xEF,0xBB,0xBF]));
    assert!(String::from_utf8(csv).unwrap().contains("\"'=copy.txt\""));
    let json:serde_json::Value=serde_json::from_reader(File::open(fixture.path("report.json")).unwrap()).unwrap();
    assert_eq!(json.as_array().unwrap().len(),3);
    assert_eq!(json[0]["volumeId"],"drive-a");
}

#[test]
fn inventory_brotli_gzip_encryption_and_settings() {
    let fixture=Fixture::new();
    let catalog=fixture.catalog("catalog");
    let volume=fixture.volume("drive");
    let root=Path::new(&volume.mount);
    fs::write(root.join("Budget report.txt"),b"cloudsentinel budget report").unwrap();
    scan(&catalog,&volume);
    catalog.update_volume_meta("drive","portable note",4).unwrap();

    let saved=SavedSearch{name:"Reports".into(),query:"bu ext:txt".into(),filters:SearchFilter::default()};
    catalog.save_saved_searches(&[saved]).unwrap();
    let loaded=catalog.load_saved_searches().unwrap();
    assert_eq!(catalog.search(&loaded[0].search_filter(),100).unwrap().len(),1);
    let saved_json=fs::read_to_string(catalog.data_dir.join("saved-searches.json")).unwrap();
    assert!(saved_json.contains("\"IncludeRemoved\""));

    let destination=fixture.catalog("destination");
    let archive=fixture.path("archive.fiinventory");
    catalog.export_inventory("drive",&archive).unwrap();
    let bytes=fs::read(&archive).unwrap();
    assert!(bytes.starts_with(b"FINVBR1"));
    let mut json=String::new();
    brotli::Decompressor::new(&bytes[7..],4096).read_to_string(&mut json).unwrap();
    assert!(json.contains("\"Format\":\"FileInventory\""));
    let id=destination.import_inventory(&archive).unwrap();
    assert!(id.starts_with("import:"));
    assert_eq!(search(&destination,"bu ext:txt"),["Budget report.txt"]);
    let offline=destination.volumes().unwrap().remove(0);
    assert!(!offline.online);
    assert_eq!(offline.note,"portable note");

    let gzip=fixture.path("archive.finv.gz");
    catalog.export_inventory("drive",&gzip).unwrap();
    assert_eq!(destination.import_inventory(&gzip).unwrap(),id);
    let encrypted=fixture.path("archive.finv.enc");
    encrypt_snapshot(&archive,&encrypted,"pässphrase").unwrap();
    assert!(fs::read(&encrypted).unwrap().starts_with(b"FINVENC1"));
    let clear=fixture.path("clear.fiinventory");
    assert!(decrypt_snapshot(&encrypted,&clear,"wrong").is_err());
    assert!(!clear.exists());
    decrypt_snapshot(&encrypted,&clear,"pässphrase").unwrap();
    assert_eq!(fs::read(&clear).unwrap(),bytes);

    let broken=fixture.path("broken.finv.gz");
    let mut output=flate2::write::GzEncoder::new(File::create(&broken).unwrap(),flate2::Compression::default());
    let (records,_)=json.trim_end().rsplit_once('\n').unwrap();
    writeln!(output,"{records}\n99").unwrap();
    output.finish().unwrap();
    assert!(destination.import_inventory(&broken).is_err());
    assert_eq!(search(&destination,"bu"),["Budget report.txt"]);

    let cloud=fixture.path("cloud");
    let config=CloudSyncConfig{folder:cloud.clone(),passphrase:"pässphrase".into(),..Default::default()};
    config.save(&catalog.data_dir).unwrap();
    assert_eq!(CloudSyncConfig::load(&catalog.data_dir).folder,cloud);
    let discovery=sync_inventory(&catalog,&config,&cancel()).unwrap();
    assert_eq!((discovery.exported,discovery.imported,discovery.errors.len()),(1,0,0));
    let copy=fixture.catalog("cloud-copy");
    let first=sync_inventory(&copy,&config,&cancel()).unwrap();
    assert_eq!((first.exported,first.imported,first.errors.len()),(0,1,0));
    assert_eq!(sync_inventory(&copy,&config,&cancel()).unwrap().skipped,1);

    let content=ContentIndex::new(catalog.clone());
    content.scan(&ContentScanOptions::default(),cancel(),|_|{}).unwrap();
    let exported=sync_content(&content,&config,&cancel()).unwrap();
    assert_eq!((exported.exported,exported.errors.len()),(1,0));
    let archive=fs::read_dir(&cloud).unwrap().map(|entry|entry.unwrap().path())
        .find(|path|path.extension().is_some_and(|ext|ext=="ficontent")).unwrap();
    fs::rename(archive,cloud.join("RemotePC - ContentIndex.ficontent")).unwrap();
    let remote_index=ContentIndex::new(copy.clone());
    let imported=sync_content(&remote_index,&config,&cancel()).unwrap();
    assert_eq!((imported.imported,imported.errors.len()),(1,0));
    let results=remote_index.search("cloudsentinel",None,None,10,&ContentSearchOptions::default()).unwrap();
    assert_eq!(results.hits[0].volume,id);
    assert!(!results.hits[0].online);
    assert!(results.hits[0].snippet.contains("budget report"));
    let db=copy.open().unwrap();
    db.execute("UPDATE volumes SET note=?1 WHERE id=?2",params!["local note",id]).unwrap();
    assert_eq!(copy.volumes().unwrap()[0].note,"local note");
}




