use crate::{treemap, AppWindow, DataRow, DriveChoice, Palette, ThemeMode, Tile, TreeRow};
use file_inventory::{
    catalog::{Catalog, FileEntry, FileSort, FileSortColumn, FolderNode, SavedSearch, ScanRecord, SearchFilter, Volume},
    content::{ContentIndex, ContentMatchMode, ContentScanMode, ContentScanOptions, ContentSearchHit, ContentSearchOptions},
    duplicates::{self, DuplicateGroup, DuplicateOptions},
    export::{self, InventoryExportFormat},
    scanner, sync::{self, CloudProvider, CloudSyncConfig}, updates,
    util::{self, Theme}, EngineResult,
};
use slint::{Color, ComponentHandle, ModelRc, SharedString, Timer, TimerMode, VecModel, Weak};
use std::{
    cell::RefCell,
    collections::{HashMap, HashSet},
    path::PathBuf,
    rc::Rc,
    sync::{atomic::{AtomicBool, AtomicU64, Ordering}, mpsc, Arc},
    thread,
    time::{Duration, Instant},
};

const ROW_LIMIT: usize = 50_000;
const PAGE_SIZE: usize = 2_000;
type ResultHandler = Box<dyn FnOnce(&mut State, &AppWindow) + Send>;
type FolderKey = (String, String);

const COMMANDS: &[(&str, &str)] = &[
    ("Scan drives", "picker"),
    ("Deep Search · Ctrl+Shift+F", "deep"),
    ("Drive Library", "library"),
    ("Duplicates", "duplicates"),
    ("Changes", "changes"),
    ("Storage Map · Ctrl+M", "map"),
    ("Sync now", "sync"),
    ("Export Report (HTML/CSV/JSON)…", "export"),
    ("Switch Theme", "theme"),
    ("Release Notes & About", "about"),
    ("Check for Updates", "updates"),
];
const MORE: &[(&str, &str)] = &[
    ("Storage Map · Ctrl+M", "map"),
    ("Command Palette · Ctrl+K", "commands"),
    ("Export Report (HTML/CSV/JSON)…", "export"),
    ("Check for Updates", "updates"),
    ("Release Notes & About", "about"),
];

struct Job {

    cancel: Arc<AtomicBool>,
}

#[derive(Clone)]
struct BrowserNode {
    key: FolderKey,
    name: String,
    depth: usize,
    expandable: bool,
}

struct State {
    catalog: Catalog,
    tx: mpsc::Sender<ResultHandler>,
    rx: mpsc::Receiver<ResultHandler>,
    search_timer: Timer,
    refresh_timer: Timer,
    theme: Theme,
    visible: Option<Vec<String>>,
    volumes: Vec<Volume>,
    current: Option<FolderKey>,
    tree: Vec<BrowserNode>,
    expanded: HashSet<FolderKey>,
    children: HashMap<FolderKey, Vec<FolderNode>>,
    files: Vec<FileEntry>,
    saved: Vec<SavedSearch>,
    job: Option<Job>,
    query_generation: Arc<AtomicU64>,
    page_offset: usize,
    deep_generation: Arc<AtomicU64>,
    estimate_generation: u64,
    overlay_generation: u64,
    map_generation: u64,
    drive_refreshing: bool,
    browser_loaded: bool,
    choices: Vec<Volume>,
    choice_indices: Vec<Option<usize>>,
    local_host: String,
    checked: HashSet<String>,
    commands: Vec<&'static str>,
    groups: Vec<DuplicateGroup>,
    dialog_files: Vec<FileEntry>,
    history: Vec<ScanRecord>,
    hits: Vec<ContentSearchHit>,
    deep_volumes: Vec<Volume>,
    map_volumes: Vec<Volume>,
    map_path: String,
    tiles: Vec<treemap::TreemapRect>,
    providers: Vec<CloudProvider>,
    config: CloudSyncConfig,
    pending_import: Option<PathBuf>,
    removing: Option<Volume>,
    release_url: String,
}

pub fn bind(app: &AppWindow, catalog: Catalog) {
    let (tx, rx) = mpsc::channel();
    let state = Rc::new(RefCell::new(State {
        theme: util::load_theme(&catalog.data_dir),
        visible: util::load_selected_drives(&catalog.data_dir),
        saved: catalog.load_saved_searches().unwrap_or_default(),
        config: CloudSyncConfig::load(&catalog.data_dir),
        catalog, tx, rx,
        search_timer: Timer::default(), refresh_timer: Timer::default(),
        volumes: Vec::new(), current: None, tree: Vec::new(), expanded: HashSet::new(), children: HashMap::new(),
        files: Vec::new(), job: None, query_generation: Arc::default(), page_offset: 0, deep_generation: Arc::default(), estimate_generation: 0,
        overlay_generation: 0, map_generation: 0, drive_refreshing: false, browser_loaded: false,
        choices: Vec::new(), choice_indices: Vec::new(),
        local_host: std::env::var("COMPUTERNAME").or_else(|_| std::env::var("HOSTNAME")).unwrap_or_default(),
        checked: HashSet::new(), commands: Vec::new(), groups: Vec::new(),
        dialog_files: Vec::new(), history: Vec::new(), hits: Vec::new(), deep_volumes: Vec::new(), map_volumes: Vec::new(),
        map_path: String::new(), tiles: Vec::new(), providers: Vec::new(), pending_import: None,
        removing: None, release_url: String::new(),
    }));
    let weak = app.as_weak();
    let actions = state.clone();
    app.on_action(move |name, index| {
        if let Some(app) = weak.upgrade() {
            actions.borrow_mut().dispatch(&app, name.as_str(), index.max(0) as usize);
        }
    });
    let mut state = state.borrow_mut();
    state.apply_theme(app);
    state.show_saved(app);
    state.refresh_drives(app, true);
    let weak = app.as_weak();
    state.refresh_timer.start(TimerMode::Repeated, Duration::from_secs(60), move || {
        if let Some(app) = weak.upgrade() {
            app.invoke_action("tick".into(), 0);
        }
    });
}

impl State {
    fn work<T: Send + 'static>(
        &self, app: &AppWindow,
        task: impl FnOnce(Catalog) -> EngineResult<T> + Send + 'static,
        done: impl FnOnce(&mut State, &AppWindow, EngineResult<T>) + Send + 'static,
    ) {
        let catalog = self.catalog.clone();
        let tx = self.tx.clone();
        let weak = app.as_weak();
        thread::spawn(move || {
            let result = task(catalog);
            if tx.send(Box::new(move |state, app| done(state, app, result))).is_ok() {
                let _ = slint::invoke_from_event_loop(move || {
                    if let Some(app) = weak.upgrade() {
                        app.invoke_action("worker-completed".into(), 0);
                    }
                });
            }
        });
    }

    fn dispatch(&mut self, app: &AppWindow, action: &str, i: usize) {
        let result = self.handle(app, action, i);
        if let Err(error) = result {
            message(app, error.to_string());
        }
    }

    fn handle(&mut self, app: &AppWindow, action: &str, i: usize) -> EngineResult<()> {
        match action {
            "worker-completed" => while let Ok(done) = self.rx.try_recv() { done(self, app); },
            "tick" => { self.apply_theme(app); self.refresh_drives(app, true); }
            "theme" => {
                self.theme = match self.theme { Theme::System => Theme::Light, Theme::Light => Theme::Dark, Theme::Dark => Theme::DarkBlue, Theme::DarkBlue => Theme::System };
                util::save_theme(&self.catalog.data_dir, self.theme)?;
                self.apply_theme(app);
            }
            "search" => {
                self.query_generation.fetch_add(1, Ordering::Relaxed);
                let weak = app.as_weak();
                self.search_timer.start(TimerMode::SingleShot, Duration::from_millis(180), move || {
                    if let Some(app) = weak.upgrade() { app.invoke_action("search-now".into(), 0); }
                });
            }
            "search-now" => { self.search_timer.stop(); self.query(app)?; }
            "file-sort" => {
                let ascending = app.get_file_sort_column() != i as i32 || !app.get_file_sort_ascending();
                app.set_file_sort_column(i as i32);
                app.set_file_sort_ascending(ascending);
                self.search_timer.stop();
                self.query(app)?;
            }
            "page-back" => self.query_page(app, self.page_offset.saturating_sub(PAGE_SIZE))?,
            "page-next" => self.query_page(app, self.page_offset.saturating_add(PAGE_SIZE))?,
            "tree-select" | "tree-toggle" => {
                if let Some(node) = self.tree.get(i).cloned() {
                    if action == "tree-toggle" && node.expandable {
                        if !self.expanded.remove(&node.key) {
                            self.expanded.insert(node.key.clone());
                            self.load_children(app, node.key);
                        }
                        self.render_tree(app);
                    } else { self.navigate(app, node.key)?; }
                }
            }
            "up" => if let Some((volume, rel)) = &self.current {
                self.navigate(app, (volume.clone(), parent(rel)))?;
            },
            "schedule" => if let Some((volume, _)) = &self.current {
                let hours: f64 = app.get_hours().trim().parse().map_err(|_| "Enter a valid number of hours")?;
                if app.get_schedule() && (!hours.is_finite() || hours <= 0.0) {
                    return Err("Enter a refresh interval greater than zero".into());
                }
                let (volume, enabled) = (volume.clone(), app.get_schedule());
                self.work(app, move |catalog| Ok(catalog.update_volume_schedule(&volume, enabled, hours)?),
                    |state, app, result| match result {
                        Ok(()) => { state.refresh_drives(app, false); message(app, "Refresh schedule saved"); }
                        Err(error) => message(app, error.to_string()),
                    });
            },
            "file-select" => self.annotation(app, i),
            "file-open" => if let Some(file) = self.files.get(i).filter(|file| file.is_directory()) {
                self.navigate(app, (file.volume.clone(), file.rel.clone()))?;
            },
            "copy" | "reveal" => if let Some(file) = selected(&self.files, app.get_selection()) {
                path_action(app, action == "copy", &file.full_path().to_string_lossy())?;
            },
            "annotation" => if let Some(file) = selected(&self.files, app.get_selection()) {
                self.catalog.save_annotation(&file.volume, &file.rel, &app.get_tags(), &app.get_note())?;
                message(app, "Tags and note saved");
            },
            "saved" => if let Some(search) = i.checked_sub(1).and_then(|index| self.saved.get(index)) {
                let filter = search.search_filter();
                app.set_query(filter.query.into());
                app.set_extension(filter.extension.unwrap_or_default().into());
                app.set_minimum(filter.min.map(|v| v.to_string()).unwrap_or_default().into());
                app.set_maximum(filter.max.map(|v| v.to_string()).unwrap_or_default().into());
                app.set_online(match filter.online { Some(true) => 1, Some(false) => 2, None => 0 });
                app.set_removed(filter.include_removed);
                self.query(app)?;
            },
            "close" => self.close(app),
            "picker" | "library" | "scope" => self.open_drives(app, action),
            "scope-all" => {
                self.checked = self.choices.iter().map(|v| v.id.clone()).collect();
                self.save_scope(app)?;
                self.current = None;
                self.render_tree(app);
                self.show_volume(app);
                self.query(app)?;
                self.close(app);
            }
            "drive-toggle" | "drive-select" | "drive-all" | "drive-none" | "drive-apply" |
            "drive-save" | "drive-import" | "drive-export" | "drive-remove" => self.drive_action(app, action, i)?,
            "deep" => {
                self.open(app, "deep", "Deep Search");
                self.deep_drives(app);
                self.estimate(app);
                if !app.get_deep_query().trim().is_empty() { self.deep_search(app); }
            }
            "estimate" => self.estimate(app),
            "deep-search" => self.deep_search(app),
            "deep-sort" => {
                let ascending = app.get_deep_sort_column() != i as i32 || !app.get_deep_sort_ascending();
                app.set_deep_sort_column(i as i32);
                app.set_deep_sort_ascending(ascending);
                let selected_id = selected(&self.hits, app.get_deep_selection()).map(|hit| hit.file_id);
                self.render_deep_hits(app, selected_id);
            }
            "deep-copy" | "deep-reveal" => if let Some(hit) = selected(&self.hits, app.get_deep_selection()) {
                path_action(app, action == "deep-copy", &hit.full_path.to_string_lossy())?;
            },
            "content-scan" => self.scan_content(app),
            "content-export" | "content-import" => self.content_exchange(app, action == "content-export"),
            "sync" if app.get_syncing() => self.cancel(app),
            "sync" | "content-sync" => self.synchronize(app, action == "content-sync"),
            "cancel" => self.cancel(app),
            "sync-settings" => {
                self.open(app, action, "Cloud Sync Settings");
                app.set_dialog_field(self.config.folder.to_string_lossy().into_owned().into());
                app.set_dialog_second(self.config.passphrase.clone().into());
                app.set_dialog_flag(self.config.sync_content_index);
                app.set_dialog_second_flag(self.config.auto_sync_after_scan);
            }
            "sync-save" => {
                let config = CloudSyncConfig {
                    folder: PathBuf::from(app.get_dialog_field().trim()), passphrase: app.get_dialog_second().to_string(),
                    auto_sync_after_scan: app.get_dialog_second_flag(), sync_content_index: app.get_dialog_flag(),
                };
                config.save(&self.catalog.data_dir)?;
                self.config = config;
                self.close(app);
                message(app, "Sync settings saved");
            }
            "sync-browse" => {
                let generation = self.overlay_generation;
                self.work(app, |_| Ok(pick(Picker::Folder, "", "")), move |state, app, result| {
                    if state.overlay_generation == generation {
                        if let Ok(Some(folder)) = result { app.set_dialog_field(folder.to_string_lossy().into_owned().into()); }
                    }
                });
            }
            "sync-detect" => {
                let generation = self.overlay_generation;
                self.work(app, |_| Ok(sync::detect_providers()), move |state, app, result| {
                    if state.overlay_generation == generation {
                        state.providers = result.unwrap_or_default();
                        app.set_dialog_drives(model(state.providers.iter().map(|p| format!("{} · {}", p.name, p.root_path.display()).into())));
                        if let Some(provider) = state.providers.first() {
                            app.set_dialog_field(provider.suggested_folder.to_string_lossy().into_owned().into());
                        } else { message(app, "No supported cloud folders detected"); }
                    }
                });
            }
            "sync-provider" => if let Some(provider) = self.providers.get(i) {
                app.set_dialog_field(provider.suggested_folder.to_string_lossy().into_owned().into());
            },
            "duplicates" => { self.open(app, action, "Duplicates"); self.duplicate_scan(app, false); }
            "duplicate-scan" | "duplicate-verify" => self.duplicate_scan(app, action == "duplicate-verify"),
            "changes" => self.load_history(app),
            "copies" => self.load_copies(app),
            "dialog-select" => self.dialog_select(app, i),
            "dialog-copy" | "dialog-reveal" => {
                let index = if app.get_overlay() == "copies" { app.get_dialog_selection() } else { app.get_detail_selection() };
                if let Some(file) = selected(&self.dialog_files, index) {
                    path_action(app, action == "dialog-copy", &file.full_path().to_string_lossy())?;
                }
            }
            "map" => {
                self.open(app, action, "Storage Map");
                self.map_volumes = self.shown_volumes().cloned().collect();
                app.set_dialog_drives(model(self.map_volumes.iter().map(|volume| volume_label(volume).into())));
                app.set_map_drive(self.current.as_ref().and_then(|(id, _)| self.map_volumes.iter().position(|volume| volume.id == *id)).unwrap_or(0) as i32);
                self.map_path = self.current.as_ref().map(|(_, rel)| rel.clone()).unwrap_or_default();
                self.load_map(app);
            }
            "map-drive" => { self.map_path.clear(); self.load_map(app); }
            "map-up" => { self.map_path = parent(&self.map_path); self.load_map(app); }
            "map-tile" => if let Some(tile) = self.tiles.get(i).filter(|tile| tile.is_dir) {
                self.map_path = tile.rel.clone();
                self.load_map(app);
            },
            "commands" | "more" => {
                self.open(app, action, if action == "more" { "More" } else { "Command Palette" });
                self.filter_commands(app);
            }
            "command-filter" => self.filter_commands(app),
            "command-run" => if let Some(&command) = self.commands.get(i) {
                self.close(app);
                self.handle(app, command, 0)?;
            },
            "about" => self.about(app),
            "updates" => self.check_updates(app),
            "update-open" => open_url(&self.release_url)?,
            "export" => if self.job.is_none() { self.open(app, action, "Export Report"); },
            "save-search" => { self.filter(app)?; self.open(app, action, "Save Search"); }
            "dialog-confirm" => self.confirm(app)?,
            _ => {}
        }
        Ok(())
    }

    fn apply_theme(&self, app: &AppWindow) {
        let palette = app.global::<Palette>();
        palette.set_system_dark(util::detect_system_dark_mode());
        let (mode, label) = match self.theme {
            Theme::System => (ThemeMode::System, "System"), Theme::Light => (ThemeMode::Light, "Light"),
            Theme::Dark => (ThemeMode::Dark, "Dark"), Theme::DarkBlue => (ThemeMode::DarkBlue, "Dark Blue"),
        };
        palette.set_mode(mode);
        app.set_theme_label(label.into());
        crate::set_titlebar(app, palette.get_dark());
    }

    fn open(&mut self, app: &AppWindow, mode: &str, title: &str) {
        self.overlay_generation += 1;
        app.set_dialog_title(title.into());
        app.set_dialog_status("".into());
        self.dialog_files.clear();
        app.set_dialog_field("".into());
        app.set_dialog_second("".into());
        app.set_dialog_flag(false);
        app.set_dialog_second_flag(false);
        app.set_dialog_rows(model([]));
        app.set_dialog_details(model([]));
        app.set_dialog_drives(model([]));
        app.set_dialog_selection(-1);
        app.set_detail_selection(-1);
        app.set_overlay(mode.into());
    }

    fn close(&mut self, app: &AppWindow) {
        self.deep_generation.fetch_add(1, Ordering::Relaxed);
        self.overlay_generation += 1;
        self.removing = None;
        self.pending_import = None;
        app.set_overlay("".into());
        app.set_dialog_field("".into());
        app.set_dialog_second("".into());
    }

    fn filter(&self, app: &AppWindow) -> EngineResult<SearchFilter> {
        Ok(SearchFilter {
            query: app.get_query().trim().to_owned(),
            extension: nonempty(app.get_extension().trim()),
            min: number(app.get_minimum().trim())?,
            max: number(app.get_maximum().trim())?,
            online: match app.get_online() { 1 => Some(true), 2 => Some(false), _ => None },
            drives: self.visible.clone(),
            include_removed: app.get_removed(),
            ..SearchFilter::default()
        })
    }

    fn query(&mut self, app: &AppWindow) -> EngineResult<()> {
        self.query_page(app, 0)
    }

    fn query_page(&mut self, app: &AppWindow, offset: usize) -> EngineResult<()> {
        let requests = self.query_generation.clone();
        let generation = requests.fetch_add(1, Ordering::Relaxed) + 1;
        let columns = [FileSortColumn::Name, FileSortColumn::Extension, FileSortColumn::Path,
            FileSortColumn::Volume, FileSortColumn::Status, FileSortColumn::Size, FileSortColumn::Modified];
        let sort = selected(&columns, app.get_file_sort_column()).map(|&column| FileSort {
            column, ascending: app.get_file_sort_ascending(),
        });
        let filter = self.filter(app).inspect_err(|_| app.set_page_busy(false))?;
        let folder = self.current.clone().filter(|_| filter.query.is_empty() && filter.extension.is_none()
            && filter.min.is_none() && filter.max.is_none() && filter.online.is_none());
        app.set_page_busy(true);
        app.set_result_status("Loading…".into());
        self.work(app, move |catalog| {
            if requests.load(Ordering::Relaxed) != generation { return Ok(None); }
            let started = Instant::now();
            let files = match folder {
                Some((volume, rel)) => catalog.browse_folder_page_sorted(&volume, &rel, filter.include_removed, offset, PAGE_SIZE + 1, sort)?,
                None => catalog.search_page_sorted(&filter, offset, PAGE_SIZE + 1, sort)?,
            };
            Ok(Some((files, started.elapsed())))
        }, move |state, app, result| {
            if state.query_generation.load(Ordering::Relaxed) != generation { return; }
            app.set_page_busy(false);
            match result {
                Ok(Some((mut files, elapsed))) => {
                    let more = files.len() > PAGE_SIZE;
                    files.truncate(PAGE_SIZE);
                    state.page_offset = offset;
                    app.set_page_back(offset > 0);
                    app.set_page_next(more);
                    let count = if files.is_empty() || (offset == 0 && !more) {
                        format!("{} items", files.len())
                    } else {
                        format!("Items {}–{}", offset + 1, offset + files.len())
                    };
                    app.set_result_status(format!("{count} · {} ms", elapsed.as_millis()).into());
                    let previous = selected(&state.files, app.get_selection()).map(|file| file.id);
                    let index = previous.and_then(|id| files.iter().position(|file| file.id == id));
                    app.set_rows(model(files.iter().map(file_row)));
                    state.files = files;
                    app.set_selection(index.map_or(-1, |i| i as i32));
                    if index.is_none() { app.set_tags("".into()); app.set_note("".into()); }
                }
                Ok(None) => {}
                Err(error) => app.set_result_status(error.to_string().into()),
            }
        });
        Ok(())
    }

    fn show_saved(&self, app: &AppWindow) {
        app.set_saved(model(std::iter::once(SharedString::from("Saved searches")).chain(self.saved.iter().map(|s| s.name.as_str().into()))));
        app.set_saved_index(0);
    }

    fn annotation(&self, app: &AppWindow, i: usize) {
        app.set_tags("".into());
        app.set_note("".into());
        if let Some(file) = self.files.get(i) {
            let (id, volume, rel) = (file.id, file.volume.clone(), file.rel.clone());
            self.work(app, move |catalog| Ok(catalog.get_annotation(&volume, &rel)?), move |state, app, result| {
                if selected(&state.files, app.get_selection()).is_some_and(|file| file.id == id) {
                    match result {
                        Ok(annotation) => { app.set_tags(annotation.tags.into()); app.set_note(annotation.note.into()); }
                        Err(error) => message(app, error.to_string()),
                    }
                }
            });
        }
    }

    fn shown_volumes(&self) -> impl Iterator<Item = &Volume> {
        self.volumes.iter().filter(|v| self.visible.as_ref().is_none_or(|scope| scope.contains(&v.id)))
    }

    fn refresh_drives(&mut self, app: &AppWindow, schedule: bool) {
        if self.drive_refreshing { return; }
        self.drive_refreshing = true;
        self.work(app, move |catalog| {
            if schedule { Ok(catalog.discover_volumes()?) }
            else { Ok(catalog.volumes()?) }
        }, move |state, app, result| {
            state.drive_refreshing = false;
            match result {
                Ok(volumes) => {
                    if !state.browser_loaded || state.volumes != volumes || !schedule {
                        state.volumes = volumes;
                        state.refresh_browser(app);
                    }
                    if schedule && state.job.is_none() {
                        let now = util::now_ms();
                        let due = state.volumes.iter().filter(|v| {
                            v.online && v.enabled && now.saturating_sub(v.attempted.unwrap_or(0)) as f64 >= v.hours * 3_600_000.0
                        }).cloned().collect::<Vec<_>>();
                        if !due.is_empty() { state.scan_drives(app, due); }
                    }
                }
                Err(error) => message(app, error.to_string()),
            }
        });
    }

    fn refresh_browser(&mut self, app: &AppWindow) {
        let first = self.shown_volumes().next().map(|v| (v.id.clone(), String::new()));
        if !self.browser_loaded || self.current.as_ref().is_some_and(|(id, _)| !self.shown_volumes().any(|v| v.id == *id)) {
            self.current = first;
        }
        self.browser_loaded = true;
        app.set_scope_label(match &self.visible {
            None => "All drives".into(),
            Some(ids) if ids.is_empty() => "No drives".into(),
            Some(ids) => format!("{} selected", ids.len()).into(),
        });
        self.render_tree(app);
        self.show_volume(app);
        if let Err(error) = self.query(app) { message(app, error.to_string()); }
    }

    fn navigate(&mut self, app: &AppWindow, key: FolderKey) -> EngineResult<()> {
        self.current = Some(key.clone());
        let mut rel = key.1.clone();
        while !rel.is_empty() {
            rel = parent(&rel);
            let ancestor = (key.0.clone(), rel.clone());
            if self.expanded.insert(ancestor.clone()) { self.load_children(app, ancestor); }
        }
        self.render_tree(app);
        self.show_volume(app);
        self.query(app)
    }

    fn load_children(&self, app: &AppWindow, key: FolderKey) {
        let query = key.clone();
        self.work(app, move |catalog| Ok(catalog.child_folders(&query.0, &query.1)?), move |state, app, result| {
            match result {
                Ok(children) => { state.children.insert(key, children); state.render_tree(app); }
                Err(error) => message(app, error.to_string()),
            }
        });
    }

    fn render_tree(&mut self, app: &AppWindow) {
        let mut tree = Vec::new();
        for volume in self.shown_volumes() {
            let mut stack = vec![BrowserNode {
                key: (volume.id.clone(), String::new()), name: volume_label(volume), depth: 0, expandable: true,
            }];
            while let Some(node) = stack.pop() {
                if self.expanded.contains(&node.key) {
                    if let Some(children) = self.children.get(&node.key) {
                        stack.extend(children.iter().rev().map(|child| BrowserNode {
                            key: (volume.id.clone(), child.rel.clone()), name: child.name.clone(),
                            depth: node.depth + 1, expandable: child.has_children,
                        }));
                    }
                }
                tree.push(node);
            }
        }
        app.set_tree(model(tree.iter().map(|n| TreeRow {
            title: n.name.as_str().into(), depth: n.depth as i32,
            marker: if !n.expandable { "" } else if self.expanded.contains(&n.key) { "▾" } else { "▸" }.into(),
        })));
        app.set_tree_selection(self.current.as_ref().and_then(|key| tree.iter().position(|n| &n.key == key)).map_or(-1, |i| i as i32));
        self.tree = tree;
    }

    fn show_volume(&self, app: &AppWindow) {
        let active = self.current.as_ref().and_then(|(id, rel)| {
            self.volumes.iter().find(|v| &v.id == id).map(|volume| (volume, rel))
        });
        app.set_has_volume(active.is_some());
        if let Some((v, rel)) = active {
            app.set_schedule(v.enabled);
            app.set_hours(v.hours.to_string().into());
            app.set_path(if rel.is_empty() { v.mount.clone() } else { format!("{}{}", v.mount, rel) }.into());
            app.set_volume_details(format!("Host: {}\nType: {}\nLast attempt: {}\nLast successful refresh: {}\nOutcome: {}\nLocation note: {}",
                v.host, if v.removable { "Removable" } else { "Fixed" },
                v.attempted.map_or_else(|| "—".into(), util::format_local_time),
                v.succeeded.map_or_else(|| "—".into(), util::format_local_time), v.outcome, v.note).into());
        } else {
            app.set_path("All drives".into());
            app.set_volume_details("Select a drive to see its inventory details.".into());
        }
    }

    fn open_drives(&mut self, app: &AppWindow, mode: &str) {
        self.open(app, mode, match mode { "picker" => "Scan Drives", "library" => "Drive Library", _ => "Drive Scope" });
        self.choices = self.volumes.iter().filter(|v| mode != "picker" || v.online).cloned().collect();
        self.checked = self.choices.iter().filter(|v| self.visible.as_ref().is_none_or(|ids| ids.contains(&v.id))).map(|v| v.id.clone()).collect();
        self.render_choices(app);
        if mode == "picker" {
            let generation = self.overlay_generation;
            self.work(app, |catalog| {
                Ok(catalog.discover_volumes()?)
            }, move |state, app, result| {
                if generation != state.overlay_generation { return; }
                match result {
                    Ok(volumes) => {
                        let had_choices = !state.choices.is_empty();
                        let previous = state.checked.clone();
                        state.volumes = volumes;
                        state.open_drives_from_current(app);
                        if had_choices {
                            state.checked = state.choices.iter().filter(|v| previous.contains(&v.id)).map(|v| v.id.clone()).collect();
                            state.render_choices(app);
                        }
                    }
                    Err(error) => message(app, error.to_string()),
                }
            });
        }
    }

    fn open_drives_from_current(&mut self, app: &AppWindow) {
        self.choices = self.volumes.iter().filter(|v| v.online).cloned().collect();
        self.checked = self.choices.iter().filter(|v| self.visible.as_ref().is_none_or(|scope| scope.contains(&v.id))).map(|v| v.id.clone()).collect();
        self.render_choices(app);
    }

    fn render_choices(&mut self, app: &AppWindow) {
        self.choice_indices.clear();
        let mut rows = Vec::new();
        for (imported, header) in [(false, "Local drives (This PC)"), (true, "Imported drives (Other PCs)")] {
            let mut group = self.choices.iter().enumerate()
                .filter(|(_, v)| is_imported(v, &self.local_host) == imported).peekable();
            if group.peek().is_none() { continue; }
            rows.push(DriveChoice { title: header.into(), is_header: true, ..Default::default() });
            self.choice_indices.push(None);
            for (index, v) in group {
                let overdue = v.freshness_days > 0 && v.succeeded.is_none_or(|at| {
                    util::now_ms().saturating_sub(at) >= i64::from(v.freshness_days) * 86_400_000
                });
                let status = if v.online { "Online" } else { "Offline" };
                let title = format!("{} · {status}{}", volume_label(v), if overdue { " · Refresh overdue" } else { "" });
                let badge = if imported { format!("Imported · {}", v.host) } else { format!("Local · {status}") };
                rows.push(DriveChoice {
                    title: title.into(), badge: badge.into(), checked: self.checked.contains(&v.id),
                    is_header: false, imported,
                });
                self.choice_indices.push(Some(index));
            }
        }
        app.set_choices(model(rows));
    }

    fn drive_choice(&self, row: i32) -> Option<&Volume> {
        let index = (*selected(&self.choice_indices, row)?)?;
        self.choices.get(index)
    }

    fn save_scope(&mut self, app: &AppWindow) -> EngineResult<()> {
        let mut ids = self.checked.iter().cloned().collect::<Vec<_>>();
        ids.sort();
        let visible = (ids.len() != self.choices.len()).then_some(ids);
        util::save_selected_drives(&self.catalog.data_dir, visible.as_deref())?;
        self.visible = visible;
        self.refresh_browser(app);
        Ok(())
    }

    fn drive_action(&mut self, app: &AppWindow, action: &str, i: usize) -> EngineResult<()> {
        let active = self.drive_choice(app.get_dialog_selection()).cloned();
        match action {
            "drive-toggle" => {
                if let Some(id) = self.drive_choice(i as i32).map(|v| v.id.clone()) {
                    if !self.checked.remove(&id) { self.checked.insert(id); }
                    self.render_choices(app);
                    if app.get_overlay() == "library" { self.save_scope(app)?; }
                }
            }
            "drive-select" => if let Some(v) = self.drive_choice(i as i32) {
                app.set_dialog_field(v.note.clone().into());
                app.set_dialog_second(v.freshness_days.to_string().into());
            },
            "drive-all" => { self.checked = self.choices.iter().map(|v| v.id.clone()).collect(); self.render_choices(app); }
            "drive-none" => { self.checked.clear(); self.render_choices(app); }
            "drive-apply" => {
                if app.get_overlay() == "scope" { self.save_scope(app)?; self.close(app); }
                else {
                    let chosen = self.choices.iter().filter(|v| self.checked.contains(&v.id)).cloned().collect::<Vec<_>>();
                    if chosen.is_empty() { return Err("Select at least one online drive".into()); }
                    self.close(app);
                    self.scan_drives(app, chosen);
                }
            }
            "drive-save" => if let Some(v) = active {
                let days = number(app.get_dialog_second().trim())?.unwrap_or(0).try_into()?;
                let note = app.get_dialog_field();
                self.catalog.update_volume_meta(&v.id, &note, days)?;
                if let Some(row) = self.choices.iter_mut().find(|row| row.id == v.id) {
                    row.note = note.to_string();
                    row.freshness_days = days;
                }
                self.refresh_drives(app, false);
                message(app, "Drive details saved");
            },
            "drive-import" => self.choose_inventory(app),
            "drive-export" => if let Some(v) = active { self.export_inventory(app, v); },
            "drive-remove" => if let Some(v) = active {
                if v.online { return Err("Only offline drives can be removed".into()); }
                self.open(app, "remove", "Remove Offline Drive");
                app.set_dialog_field(format!("Remove the inventory for {}?", volume_label(&v)).into());
                self.removing = Some(v);
            },
            _ => {}
        }
        Ok(())
    }

    fn update_library(&mut self, app: &AppWindow) {
        let generation = self.overlay_generation;
        self.work(app, |catalog| Ok(catalog.volumes()?), move |state, app, result| {
            match result {
                Ok(volumes) => {
                    state.volumes = volumes;
                    state.invalidate_tree(app);
                    state.refresh_browser(app);
                    if generation == state.overlay_generation && app.get_overlay() == "library" {
                        state.choices = state.volumes.clone();
                        state.checked = state.shown_volumes().map(|v| v.id.clone()).collect();
                        state.render_choices(app);
                        app.set_dialog_selection(-1);
                    }
                }
                Err(error) => message(app, error.to_string()),
            }
        });
    }

    fn invalidate_tree(&mut self, app: &AppWindow) {
        self.children.clear();
        for key in self.expanded.iter().cloned() { self.load_children(app, key); }
    }

    fn begin(&mut self, app: &AppWindow, kind: &'static str, text: &str) -> Option<Arc<AtomicBool>> {
        if self.job.is_some() {
            message(app, "A background operation is already running");
            return None;
        }
        let cancel = Arc::new(AtomicBool::new(false));
        self.job = Some(Job { cancel: cancel.clone() });
        app.set_busy(true);
        app.set_syncing(kind == "sync" || kind == "content-sync");
        app.set_progress(0.0);
        app.set_status(text.into());
        Some(cancel)
    }

    fn end(&mut self, app: &AppWindow, result: EngineResult<String>) {
        let cancelled = self.job.take().is_some_and(|job| job.cancel.load(Ordering::Relaxed));
        app.set_busy(false);
        app.set_syncing(false);
        app.set_progress(if result.is_ok() && !cancelled { 1.0 } else { 0.0 });
        message(app, match result {
            Ok(text) => text,
            Err(error) => if cancelled && error.downcast_ref::<std::io::Error>().is_some_and(|e| e.kind() == std::io::ErrorKind::Interrupted) {
                "Operation cancelled".into()
            } else { error.to_string() },
        });
    }

    fn cancel(&self, app: &AppWindow) {
        if let Some(job) = &self.job {
            job.cancel.store(true, Ordering::Relaxed);
            app.set_status("Cancelling…".into());
        }
    }

    fn scan_drives(&mut self, app: &AppWindow, drives: Vec<Volume>) {
        let Some(cancel) = self.begin(app, "scan", "Preparing drive scan…") else { return; };
        let weak = app.as_weak();
        self.work(app, move |catalog| {
            let mut items = 0;
            let mut omissions = 0;
            let mut outcomes = Vec::new();
            let count = drives.len();
            for (i, volume) in drives.iter().enumerate() {
                if cancel.load(Ordering::Relaxed) { break; }
                let outcome = scanner::scan(&catalog, volume, cancel.clone(), |p| {
                    let (fraction, eta) = progress(p.items, p.estimated_total.unwrap_or(0), Duration::from_millis(p.elapsed_ms));
                    let percent = p.estimated_total.map_or_else(|| "Estimating…".into(), |_| format!("{:.0}%", fraction * 100.0));
                    let text = format!("{} ({}/{}) · {} · {} items scanned · ETA {} · {} omissions", volume.mount, i + 1, count, percent, items + p.items, eta, omissions + p.omitted);
                    post_progress(&weak, &cancel, (i as f32 + fraction) / count as f32, text, false);
                });
                match outcome {
                    Ok(outcome) => { items += outcome.items; omissions += outcome.omitted; outcomes.push(format!("{} {}", volume.mount, outcome.outcome)); }
                    Err(error) => outcomes.push(format!("{} {error}", volume.mount)),
                }
            }
            Ok(format!("{} items scanned · {} omissions · {}{}", items, omissions, outcomes.join(" · "),
                if cancel.load(Ordering::Relaxed) { " · Cancelled" } else { "" }))
        }, |state, app, result| {
            let auto = result.is_ok() && state.config.auto_sync_after_scan &&
                state.job.as_ref().is_some_and(|job| !job.cancel.load(Ordering::Relaxed));
            state.end(app, result);
            state.invalidate_tree(app);
            state.refresh_drives(app, false);
            if auto { state.synchronize(app, false); }
        });
    }

    fn deep_drives(&mut self, app: &AppWindow) {
        let search_id = self.deep_volume(app.get_deep_drive());
        let scan_id = self.deep_volume(app.get_scan_drive());
        self.deep_volumes = self.volumes.clone();
        let index = |id: Option<String>| id.and_then(|id| self.deep_volumes.iter().position(|v| v.id == id))
            .map_or(0, |index| index as i32 + 1);
        app.set_deep_drives(model(std::iter::once(SharedString::from("All drives"))
            .chain(self.deep_volumes.iter().map(|v| content_volume_label(v, &self.local_host).into()))));
        app.set_deep_drive(index(search_id));
        app.set_scan_drive(index(scan_id));
    }

    fn deep_volume(&self, index: i32) -> Option<String> {
        selected(&self.deep_volumes, index - 1).map(|v| v.id.clone())
    }

    fn content_options(&self, app: &AppWindow) -> ContentScanOptions {
        ContentScanOptions {
            volume_ids: self.deep_volume(app.get_scan_drive()).map(|id| vec![id]),
            mode: match app.get_resource_mode() { 1 => ContentScanMode::Balanced, 2 => ContentScanMode::Turbo, _ => ContentScanMode::Eco },
            store_snippets: app.get_excerpts(),
            include_text: app.get_text_files(),
            include_office: app.get_office(),
            include_pdf: app.get_pdf(),
            skip_system_folders: app.get_skip_system(),
            ..ContentScanOptions::default()
        }
    }

    fn estimate(&mut self, app: &AppWindow) {
        self.estimate_generation += 1;
        let generation = self.estimate_generation;
        let options = self.content_options(app);
        app.set_deep_estimate("Estimating content scan…".into());
        self.work(app, move |catalog| {
            let index = ContentIndex::new(catalog);
            Ok((index.estimate_scan(&options)?, index.stats()?))
        }, move |state, app, result| {
            if state.estimate_generation != generation { return; }
            match result {
                Ok((estimate, stats)) => {
                    app.set_deep_estimate(format!("{} to scan · {} already indexed · {} source data · ~{} index growth",
                        estimate.to_scan, estimate.already_indexed, util::format_bytes(estimate.total_source_bytes),
                        util::format_bytes(estimate.estimated_index_bytes)).into());
                    if state.hits.is_empty() && app.get_deep_query().trim().is_empty() {
                        app.set_deep_status(format!("{} indexed files · {} DB", stats.indexed_files, util::format_bytes(stats.database_bytes)).into());
                    }
                }
                Err(error) => app.set_deep_estimate(error.to_string().into()),
            }
        });
    }

    fn deep_search(&mut self, app: &AppWindow) {
        let requests = self.deep_generation.clone();
        let generation = requests.fetch_add(1, Ordering::Relaxed) + 1;
        let local_host = self.local_host.clone();
        let query = app.get_deep_query().to_string();
        let extension = nonempty(app.get_deep_extension().trim());
        let volume = self.deep_volume(app.get_deep_drive());
        let options = ContentSearchOptions {
            mode: match app.get_match_mode() { 1 => ContentMatchMode::AllWords, 2 => ContentMatchMode::AnyWord, 3 => ContentMatchMode::ExactPhrase, _ => ContentMatchMode::Smart },
            fuzzy_matching: app.get_fuzzy(),
        };
        app.set_deep_close_match(false);
        app.set_deep_status("Searching…".into());
        self.work(app, move |catalog| {
            if requests.load(Ordering::Relaxed) != generation { return Ok(None); }
            let index = ContentIndex::new(catalog.clone());
            let started = Instant::now();
            let mut result = index.search(&query, volume.as_deref(), extension.as_deref(), ROW_LIMIT, &options)?;
            let elapsed = started.elapsed();
            if requests.load(Ordering::Relaxed) != generation { return Ok(None); }
            let labels: HashMap<_, _> = catalog.volumes()?.into_iter()
                .map(|v| (v.id.clone(), content_volume_label(&v, &local_host))).collect();
            for hit in &mut result.hits {
                if let Some(label) = labels.get(&hit.volume) { hit.volume_label.clone_from(label); }
            }
            Ok(Some((result, elapsed, index.stats()?)))
        }, move |state, app, result| {
            if state.deep_generation.load(Ordering::Relaxed) != generation { return; }
            match result {
                Ok(Some((found, elapsed, stats))) => {
                    let corrections = if found.fuzzy_corrections.is_empty() { String::new() } else { format!(" · Typo match: {}", found.fuzzy_corrections.join(", ")) };
                    app.set_deep_close_match(found.used_relaxed_match);
                    app.set_deep_status(format!("{} matches · {} ms{} · {} indexed files · {} DB{}",
                        found.hits.len(), elapsed.as_millis(), corrections,
                        stats.indexed_files, util::format_bytes(stats.database_bytes), if found.summary.is_empty() { String::new() } else { format!(" · {}", found.summary) }).into());
                    state.hits = found.hits;
                    state.render_deep_hits(app, None);
                }
                Ok(None) => {}
                Err(error) => app.set_deep_status(error.to_string().into()),
            }
        });
    }

    fn render_deep_hits(&mut self, app: &AppWindow, selected_id: Option<i64>) {
        let column = app.get_deep_sort_column();
        let descending = !app.get_deep_sort_ascending();
        let direction = |order: std::cmp::Ordering| if descending { order.reverse() } else { order };
        match column {
            0..=2 => {
                let key = |hit: &ContentSearchHit| match column {
                    0 => hit.name.to_lowercase(),
                    1 => hit.volume_label.to_lowercase(),
                    _ => hit.snippet.to_lowercase(),
                };
                if descending {
                    self.hits.sort_by_cached_key(|hit| (std::cmp::Reverse(key(hit)), hit.file_id));
                } else {
                    self.hits.sort_by_cached_key(|hit| (key(hit), hit.file_id));
                }
            }
            3 => self.hits.sort_by(|a, b| direction(a.bytes.cmp(&b.bytes)).then(a.file_id.cmp(&b.file_id))),
            4 => self.hits.sort_by(|a, b| direction(a.modified.cmp(&b.modified)).then(a.file_id.cmp(&b.file_id))),
            _ => {} // The index returns hits in relevance order.
        }
        app.set_deep_rows(model(self.hits.iter().map(|hit| DataRow {
            a: hit.name.as_str().into(), b: hit.volume_label.as_str().into(), c: hit.snippet.as_str().into(),
            d: bytes(hit.bytes).into(), e: util::format_local_time(hit.modified).into(), ..Default::default()
        })));
        let selection = selected_id.and_then(|id| self.hits.iter().position(|hit| hit.file_id == id));
        app.set_deep_selection(selection.map_or(-1, |i| i as i32));
    }

    fn scan_content(&mut self, app: &AppWindow) {
        let Some(cancel) = self.begin(app, "content-scan", "Preparing content scan…") else { return; };
        let options = self.content_options(app);
        let weak = app.as_weak();
        app.set_deep_progress(0.0);
        app.set_deep_progress_text("Preparing content scan…".into());
        self.work(app, move |catalog| {
            let start = Instant::now();
            let mut last = None;
            let outcome = ContentIndex::new(catalog).scan(&options, cancel.clone(), |p| {
                if last.is_some_and(|time: Instant| time.elapsed() < Duration::from_millis(95)) && p.processed != p.total { return; }
                last = Some(Instant::now());
                let (fraction, eta) = progress(p.processed, p.total, start.elapsed());
                post_progress(&weak, &cancel, fraction, format!("{:.0}% · {}/{} · ETA {} · {} failed", fraction * 100.0, p.processed, p.total, eta, p.failed), true);
            })?;
            Ok(format!("{} indexed · {} skipped · {} failed", outcome.indexed, outcome.skipped, outcome.failed))
        }, |state, app, result| {
            let fraction = if result.is_ok() { 1.0 } else { app.get_deep_progress() };
            state.end(app, result);
            app.set_deep_progress(fraction);
            app.set_deep_progress_text(app.get_status());
            state.estimate(app);
            if !app.get_deep_query().trim().is_empty() { state.deep_search(app); }
        });
    }

    fn content_exchange(&mut self, app: &AppWindow, exporting: bool) {
        let Some(cancel) = self.begin(app, "content-exchange", "Choose a content snapshot…") else { return; };
        let volume = self.deep_volume(app.get_scan_drive());
        self.work(app, move |catalog| {
            let mode = if exporting { Picker::Save } else { Picker::Open };
            let Some(path) = pick(mode, "Content snapshots", "ficontent") else { return Ok("Ready".into()); };
            let index = ContentIndex::new(catalog);
            if exporting {
                index.export_content_with_cancel(&path, volume.as_deref(), &cancel)?;
                Ok(format!("Exported {}", path.display()))
            } else {
                Ok(format!("Imported {} indexed files", index.import_content_with_cancel(&path, &cancel)?))
            }
        }, |state, app, result| {
            state.end(app, result);
            state.estimate(app);
            if !app.get_deep_query().trim().is_empty() { state.deep_search(app); }
        });
    }

    fn synchronize(&mut self, app: &AppWindow, content_only: bool) {
        if self.config.folder.as_os_str().is_empty() {
            self.dispatch(app, "sync-settings", 0);
            return;
        }
        let kind = if content_only { "content-sync" } else { "sync" };
        let Some(cancel) = self.begin(app, kind, "Preparing cloud sync…") else { return; };
        let config = self.config.clone();
        let weak = app.as_weak();
        self.work(app, move |catalog| {
            let started = Instant::now();
            let with_content = content_only || config.sync_content_index;
            let mut summaries = Vec::new();
            if !content_only {
                summaries.push(sync::sync_inventory_with_progress(&catalog, &config, &cancel, |done, total, name| {
                    let (fraction, eta) = progress(done as u64, total as u64, started.elapsed());
                    post_progress(&weak, &cancel, fraction * if with_content { 0.5 } else { 1.0 }, format!("Inventory sync · {:.0}% · {done}/{total} · ETA {eta} · {name}", fraction * 100.0), false);
                })?);
            }
            if with_content {
                summaries.push(sync::sync_content_with_progress(&ContentIndex::new(catalog), &config, &cancel, |done, total, name| {
                    let (fraction, eta) = progress(done as u64, total as u64, started.elapsed());
                    post_progress(&weak, &cancel, if content_only { fraction } else { 0.5 + fraction * 0.5 }, format!("Content sync · {:.0}% · {done}/{total} · ETA {eta} · {name}", fraction * 100.0), content_only);
                })?);
            }
            let errors = summaries.iter().flat_map(|s| s.errors.iter().map(String::as_str)).collect::<Vec<_>>();
            Ok(format!("{} exported · {} imported · {} skipped{}",
                summaries.iter().map(|s| s.exported).sum::<usize>(),
                summaries.iter().map(|s| s.imported).sum::<usize>(),
                summaries.iter().map(|s| s.skipped).sum::<usize>(),
                if errors.is_empty() { String::new() } else { format!(" · {}", errors.join(" · ")) }))
        }, move |state, app, result| {
            state.end(app, result);
            if content_only { app.set_deep_progress_text(app.get_status()); }
            state.update_library(app);
            if app.get_overlay() == "deep" { state.estimate(app); }
        });
    }

    fn duplicate_scan(&mut self, app: &AppWindow, verify: bool) {
        let Some(cancel) = self.begin(app, "duplicates", if verify { "Verifying SHA-256…" } else { "Finding duplicate candidates…" }) else { return; };
        app.set_dialog_status(app.get_status());
        let groups = verify.then(|| self.groups.clone());
        let generation = self.overlay_generation;
        self.work(app, move |catalog| {
            match groups {
                Some(groups) => duplicates::verify_groups(&catalog, &groups, &cancel),
                None => duplicates::find_duplicate_groups(&catalog, &DuplicateOptions::default()),
            }
        }, move |state, app, result| {
            let status = match result {
                Ok(groups) => {
                    let count = groups.len();
                    state.groups = groups;
                    if state.overlay_generation == generation {
                        app.set_dialog_rows(model(state.groups.iter().map(|group| DataRow {
                            a: format!("{} files", group.files.len()).into(),
                            b: bytes(group.bytes).into(), c: group.full_hash.as_deref().unwrap_or("Candidate").into(),
                            ..Default::default()
                        })));
                        app.set_dialog_selection(if count > 0 { 0 } else { -1 });
                        state.dialog_select(app, 0);
                    }
                    Ok(format!("{count} {}groups", if verify { "verified " } else { "candidate " }))
                }
                Err(error) => Err(error),
            };
            state.end(app, status);
        });
    }

    fn load_copies(&mut self, app: &AppWindow) {
        let Some(file) = selected(&self.files, app.get_selection()) else { return; };
        let (volume, rel) = (file.volume.clone(), file.rel.clone());
        self.open(app, "copies", "Find Possible Copies");
        app.set_dialog_status("Finding possible copies…".into());
        let generation = self.overlay_generation;
        self.work(app, move |catalog| Ok(catalog.find_possible_copies(&volume, &rel)?), move |state, app, result| {
            if generation != state.overlay_generation { return; }
            match result {
                Ok(files) => {
                    app.set_dialog_rows(model(files.iter().map(compact_file_row)));
                    app.set_dialog_selection(if files.is_empty() { -1 } else { 0 });
                    app.set_dialog_status(format!("{} possible copies", files.len()).into());
                    state.dialog_files = files;
                }
                Err(error) => message(app, error.to_string()),
            }
        });
    }

    fn load_history(&mut self, app: &AppWindow) {
        self.open(app, "changes", "Changes · Scan History");
        app.set_dialog_status("Loading scan history…".into());
        let generation = self.overlay_generation;
        self.work(app, |catalog| Ok(catalog.scan_history(None, ROW_LIMIT)?), move |state, app, result| {
            if generation != state.overlay_generation { return; }
            match result {
                Ok(history) => {
                    app.set_dialog_rows(model(history.iter().map(|s| DataRow {
                        a: state.volumes.iter().find(|v| v.id == s.volume).map(volume_label).unwrap_or_else(|| s.volume.clone()).into(),
                        b: s.outcome.as_str().into(),
                        c: format!("{} · +{} · {} modified · −{} · {} moved", util::format_local_time(s.started),
                            s.added_count, s.modified_count, s.removed_count, s.moved_count).into(),
                        d: signed_bytes(s.bytes_delta).into(),
                        ..Default::default()
                    })));
                    app.set_dialog_status(format!("{} scans", history.len()).into());
                    state.history = history;
                    if !state.history.is_empty() { app.set_dialog_selection(0); state.dialog_select(app, 0); }
                }
                Err(error) => message(app, error.to_string()),
            }
        });
    }

    fn dialog_select(&mut self, app: &AppWindow, i: usize) {
        match app.get_overlay().as_str() {
            "duplicates" => {
                self.dialog_files = self.groups.get(i).map(|group| group.files.clone()).unwrap_or_default();
                app.set_dialog_details(model(self.dialog_files.iter().map(|file| DataRow {
                    d: file.status().into(), ..compact_file_row(file)
                })));
                app.set_detail_selection(if self.dialog_files.is_empty() { -1 } else { 0 });
            }
            "changes" => if let Some(scan) = self.history.get(i) {
                let id = scan.id;
                let generation = self.overlay_generation;
                app.set_dialog_details(model([]));
                self.work(app, move |catalog| Ok(catalog.scan_changes(id, ROW_LIMIT)?), move |state, app, result| {
                    if generation != state.overlay_generation || app.get_dialog_selection() != i as i32 { return; }
                    match result {
                        Ok(changes) => app.set_dialog_details(model(changes.iter().map(|c| DataRow {
                            a: match c.kind { 1 => "Added", 2 => "Modified", 3 => "Removed", 4 => "Moved", _ => "Change" }.into(),
                            b: c.old_bytes.map_or_else(String::new, bytes).into(), c: c.rel.as_str().into(),
                            d: c.new_bytes.map_or_else(String::new, bytes).into(), ..Default::default()
                        }))),
                        Err(error) => message(app, error.to_string()),
                    }
                });
            },
            _ => {}
        }
    }

    fn load_map(&mut self, app: &AppWindow) {
        self.map_generation += 1;
        let generation = self.map_generation;
        let overlay = self.overlay_generation;
        self.tiles.clear();
        app.set_tiles(model([]));
        let Some(volume) = selected(&self.map_volumes, app.get_map_drive()) else { message(app, "No drives in scope"); return; };
        let id = volume.id.clone();
        let path = self.map_path.clone();
        app.set_dialog_field(format!("{}{}", volume.mount, path).into());
        app.set_dialog_status("Calculating storage map…".into());
        let size = app.window().size();
        let scale = app.window().scale_factor() as f64;
        let width = ((size.width as f64 / scale).min(976.0) - 36.0).max(1.0);
        let height = ((size.height as f64 / scale).min(716.0) - 153.0).max(1.0);
        self.work(app, move |catalog| Ok(treemap::layout(catalog.folder_stats_children(&id, &path)?, width, height)), move |state, app, result| {
            if state.map_generation != generation || state.overlay_generation != overlay { return; }
            match result {
                Ok(tiles) => {
                    const COLORS: [u32; 6] = [0x0078b9, 0x784ba0, 0x286c60, 0xa25431, 0x466394, 0x9a476c];
                    app.set_tiles(model(tiles.iter().map(|tile| Tile {
                        x: tile.x as f32, y: tile.y as f32, w: tile.w as f32, h: tile.h as f32, label: tile.label.as_str().into(),
                        detail: tile.size_text.as_str().into(), tint: Color::from_argb_encoded(0xff000000 | COLORS[tile.color_idx]),
                    })));
                    app.set_dialog_status(if tiles.is_empty() { "No stored bytes in this folder".into() } else { "".into() });
                    state.tiles = tiles;
                }
                Err(error) => message(app, error.to_string()),
            }
        });
    }

    fn filter_commands(&mut self, app: &AppWindow) {
        let query = app.get_dialog_field().to_lowercase();
        let list = if app.get_overlay() == "more" { MORE } else { COMMANDS };
        let matches = list.iter().filter(|(label, _)| {
            let label = label.to_lowercase();
            query.split_whitespace().all(|word| label.contains(word))
        }).collect::<Vec<_>>();
        self.commands = matches.iter().map(|(_, command)| *command).collect();
        app.set_dialog_rows(model(matches.into_iter().map(|(label, _)| DataRow { a: (*label).into(), ..Default::default() })));
    }

    fn about(&mut self, app: &AppWindow) {
        self.open(app, "about", "Release Notes & About");
        app.set_dialog_field(updates::FULL_RELEASE_NOTES.into());
        app.set_dialog_flag(!self.release_url.is_empty());
    }

    fn check_updates(&mut self, app: &AppWindow) {
        if self.job.is_some() { message(app, "Wait for the current background operation to finish"); return; }
        if app.get_overlay() != "about" { self.about(app); }
        if self.begin(app, "updates", "Checking for updates…").is_none() { return; }
        app.set_dialog_status(app.get_status());
        self.work(app, |_| updates::check_for_updates(), |state, app, result| {
            let status = result.map(|release| {
                if let Some(release) = release {
                    state.release_url = release.download_url;
                    format!("Version {} is available · {}", release.version, util::format_bytes(release.bytes))
                } else {
                    state.release_url.clear();
                    format!("File Inventory {} is up to date", updates::APP_VERSION)
                }
            });
            if app.get_overlay() == "about" { app.set_dialog_flag(!state.release_url.is_empty()); }
            state.end(app, status);
        });
    }

    fn choose_inventory(&mut self, app: &AppWindow) {
        let Some(cancel) = self.begin(app, "inventory", "Choose an inventory snapshot…") else { return; };
        let generation = self.overlay_generation;
        self.work(app, move |catalog| {
            let Some(path) = pick(Picker::Open, "Inventory snapshots", "fiinventory,finv.gz,finv.enc,enc,gz") else {
                return Ok((None, None));
            };
            if path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("enc")) {
                return Ok((Some(path), None));
            }
            let id = catalog.import_inventory_with_passphrase(&path, "", &cancel)?;
            Ok((None, Some(id)))
        }, move |state, app, result| {
            match result {
                Ok((Some(path), _)) => {
                    state.end(app, Ok("Enter the snapshot passphrase".into()));
                    if generation == state.overlay_generation {
                        state.open(app, "import-password", "Import Encrypted Inventory");
                        app.set_dialog_field(state.config.passphrase.as_str().into());
                        state.pending_import = Some(path);
                    }
                }
                Ok((_, Some(_))) => { state.end(app, Ok("Inventory imported".into())); state.update_library(app); }
                Ok(_) => state.end(app, Ok("Ready".into())),
                Err(error) => state.end(app, Err(error)),
            }
        });
    }

    fn export_inventory(&mut self, app: &AppWindow, volume: Volume) {
        let Some(cancel) = self.begin(app, "inventory", "Choose an inventory destination…") else { return; };
        self.work(app, move |catalog| {
            let Some(path) = pick(Picker::Save, "File Inventory snapshot", "fiinventory") else { return Ok("Ready".into()); };
            catalog.export_inventory_with_cancel(&volume.id, &path, &cancel)?;
            Ok(format!("Exported {}", path.display()))
        }, |state, app, result| state.end(app, result));
    }

    fn confirm(&mut self, app: &AppWindow) -> EngineResult<()> {
        match app.get_overlay().as_str() {
            "save-search" => {
                let name = app.get_dialog_field().trim().to_owned();
                if name.is_empty() { return Err("Enter a name for the saved search".into()); }
                let mut filters = self.filter(app)?;
                let query = std::mem::take(&mut filters.query);
                let search = SavedSearch { name, query, filters };
                let mut all = self.saved.clone();
                if let Some(existing) = all.iter_mut().find(|s| s.name.eq_ignore_ascii_case(&search.name)) { *existing = search; }
                else { all.push(search); }
                all.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
                self.catalog.save_saved_searches(&all)?;
                self.saved = all;
                self.show_saved(app);
                self.close(app);
                message(app, "Search saved");
            }
            "remove" => if let Some(volume) = self.removing.clone() {
                let Some(cancel) = self.begin(app, "inventory", "Removing offline drive…") else { return Ok(()); };
                self.close(app);
                self.work(app, move |catalog| {
                    if cancel.load(Ordering::Relaxed) { return Ok("Operation cancelled".into()); }
                    if catalog.discover_volumes()?.iter().any(|drive| drive.id == volume.id && drive.online) {
                        return Err("The drive is online. Disconnect it before removing its inventory.".into());
                    }
                    catalog.delete_volume(&volume.id)?;
                    Ok(format!("Removed {}", volume_label(&volume)))
                }, |state, app, result| {
                    state.end(app, result);
                    state.open_drives(app, "library");
                    state.update_library(app);
                });
            },
            "import-password" => if let Some(path) = self.pending_import.clone() {
                let password = app.get_dialog_field().to_string();
                if password.is_empty() { return Err("Enter the snapshot passphrase".into()); }
                let Some(cancel) = self.begin(app, "inventory", "Importing encrypted inventory…") else { return Ok(()); };
                self.work(app, move |catalog| {
                    catalog.import_inventory_with_passphrase(&path, &password, &cancel)?;
                    Ok("Inventory imported".into())
                }, |state, app, result| {
                    if result.is_ok() && app.get_overlay() == "import-password" { state.open_drives(app, "library"); }
                    state.end(app, result);
                    state.update_library(app);
                });
            },
            "export" => {
                let filter = self.filter(app)?;
                let folder = self.current.clone().filter(|_| filter.query.is_empty() && filter.extension.is_none()
                    && filter.min.is_none() && filter.max.is_none() && filter.online.is_none());
                let (format, extension) = match app.get_report_format() {
                    1 => (InventoryExportFormat::Csv, "csv"), 2 => (InventoryExportFormat::Json, "json"),
                    _ => (InventoryExportFormat::Html, "html"),
                };
                let Some(cancel) = self.begin(app, "report", "Choose a report destination…") else { return Ok(()); };
                self.close(app);
                self.work(app, move |catalog| {
                    let Some(path) = pick(Picker::Save, "Inventory report", extension) else { return Ok("Ready".into()); };
                    let rows = match folder {
                        Some((volume, rel)) => catalog.browse_folder(&volume, &rel, filter.include_removed, usize::MAX)?,
                        None => catalog.search(&filter, usize::MAX)?,
                    };
                    export::write(&path, &rows, format, "File Inventory Report", &cancel)?;
                    Ok(format!("Exported {} entries to {}", rows.len(), path.display()))
                }, |state, app, result| state.end(app, result));
            }
            _ => {}
        }
        Ok(())
    }
}

fn model<T: Clone + 'static>(items: impl IntoIterator<Item = T>) -> ModelRc<T> {
    ModelRc::new(VecModel::from(items.into_iter().collect::<Vec<_>>()))
}

fn selected<T>(items: &[T], index: i32) -> Option<&T> {
    usize::try_from(index).ok().and_then(|i| items.get(i))
}

fn parent(rel: &str) -> String {
    rel.rsplit_once(['\\', '/']).map_or("", |(parent, _)| parent).into()
}

fn bytes(value: i64) -> String {
    if value < 0 { "—".into() } else { util::format_bytes(value as u64) }
}

fn signed_bytes(value: i64) -> String {
    format!("{}{}", if value < 0 { "−" } else if value > 0 { "+" } else { "" }, util::format_bytes(value.unsigned_abs()))
}

fn nonempty(value: &str) -> Option<String> {
    (!value.is_empty()).then(|| value.to_owned())
}

fn number(value: &str) -> EngineResult<Option<i64>> {
    if value.is_empty() { return Ok(None); }
    let number = value.parse::<i64>().map_err(|_| "Enter a nonnegative whole number")?;
    if number < 0 { return Err("Enter a nonnegative whole number".into()); }
    Ok(Some(number))
}

fn volume_label(v: &Volume) -> String {
    if v.label.is_empty() || v.label.eq_ignore_ascii_case(&v.mount) { v.mount.clone() } else { format!("{}  {}", v.mount, v.label) }
}

fn is_imported(v: &Volume, local_host: &str) -> bool {
    !v.host.is_empty() && !v.host.eq_ignore_ascii_case(local_host)
}

fn content_volume_label(v: &Volume, local_host: &str) -> String {
    let label = volume_label(v);
    if is_imported(v, local_host) { format!("{label} · {}", v.host) } else { label }
}

fn file_row(f: &FileEntry) -> DataRow {
    DataRow {
        a: f.name.as_str().into(), b: f.extension.as_str().into(), c: f.full_path().to_string_lossy().into_owned().into(),
        d: if f.label.is_empty() { f.mount.as_str() } else { f.label.as_str() }.into(),
        e: f.status().into(), f: if f.is_directory() { "".into() } else { bytes(f.bytes).into() },
        g: util::format_local_time(f.modified).into(),
    }
}

fn compact_file_row(f: &FileEntry) -> DataRow {
    let row = file_row(f);
    DataRow { a: row.a, b: row.d, c: row.c, d: row.f, ..Default::default() }
}

fn message(app: &AppWindow, text: impl Into<SharedString>) {
    let text = text.into();
    app.set_status(text.clone());
    if app.get_overlay() == "deep" { app.set_deep_progress_text(text); }
    else if !app.get_overlay().is_empty() { app.set_dialog_status(text); }
}

fn path_action(app: &AppWindow, copy: bool, path: &str) -> EngineResult<()> {
    if copy { util::copy_to_clipboard(path)?; message(app, "Path copied"); }
    else { util::reveal_in_explorer(path)?; }
    Ok(())
}

fn progress(done: u64, total: u64, elapsed: Duration) -> (f32, String) {
    if total == 0 { return (0.0, "—".into()); }
    let fraction = (done as f64 / total as f64).clamp(0.0, 1.0);
    let eta = if fraction > 0.0 && elapsed > Duration::ZERO {
        let seconds = (elapsed.as_secs_f64() * (1.0 - fraction) / fraction).min(365.0 * 86_400.0);
        util::format_time_left(Duration::from_secs_f64(seconds))
    } else { "—".into() };
    (fraction as f32, eta)
}

fn post_progress(weak: &Weak<AppWindow>, cancel: &Arc<AtomicBool>, fraction: f32, text: String, content: bool) {
    let weak = weak.clone();
    let cancel = cancel.clone();
    let _ = slint::invoke_from_event_loop(move || {
        if cancel.load(Ordering::Relaxed) { return; }
        if let Some(app) = weak.upgrade() {
            app.set_progress(fraction);
            app.set_status(text.as_str().into());
            if content { app.set_deep_progress(fraction); app.set_deep_progress_text(text.into()); }
        }
    });
}

enum Picker { Open, Save, Folder }

#[cfg(windows)]
fn pick(mode: Picker, name: &str, extension: &str) -> Option<PathBuf> {
    let mut dialog = rfd::FileDialog::new();
    if !extension.is_empty() { dialog = dialog.add_filter(name, &extension.split(',').collect::<Vec<_>>()); }
    match mode {
        Picker::Open => dialog.pick_file(),
        Picker::Save => {
            let path = dialog.set_file_name(format!("FileInventory.{extension}")).save_file()?;
            Some(if path.extension().is_none() { path.with_extension(extension) } else { path })
        }
        Picker::Folder => dialog.pick_folder(),
    }
}

#[cfg(not(windows))]
fn pick(_mode: Picker, _name: &str, _extension: &str) -> Option<PathBuf> { None }

fn open_url(url: &str) -> EngineResult<()> {
    if url.is_empty() { return Ok(()); }
    #[cfg(windows)]
    std::process::Command::new("explorer.exe").arg(url).spawn()?;
    #[cfg(not(windows))]
    std::process::Command::new("xdg-open").arg(url).spawn()?;
    Ok(())
}

