#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::{cell::RefCell, path::PathBuf, rc::Rc, sync::{Arc,atomic::AtomicBool}, time::Instant};
use file_inventory::{EngineResult,catalog::{Catalog,Volume},scanner,semantic::{self, SemanticPack},util};

mod guide;
mod treemap;
mod ui_state;

slint::include_modules!();

fn main() -> EngineResult<()> {
    let mut args=std::env::args_os().skip(1);
    let mut mode = args.next();
    if mode.as_deref().is_some_and(|arg| arg == "--benchmark") {
        let root=args.next().map(PathBuf::from).unwrap_or(std::env::current_dir()?);
        let scratch=std::env::temp_dir().join(format!("file-inventory-benchmark-{:032x}",rand::random::<u128>()));
        std::fs::create_dir(&scratch)?;
        let scratch=Scratch(scratch);
        let catalog=Catalog::new(&scratch.0);
        let mount=root.canonicalize()?.to_string_lossy().into_owned();
        let volume=Volume{id:"benchmark".into(),mount,label:"Benchmark".into(),online:true,..Default::default()};
        let started=Instant::now();
        let result=scanner::scan(&catalog,&volume,Arc::new(AtomicBool::new(false)),|_|{})?;
        let elapsed=started.elapsed().as_secs_f64();
        println!("{} entries, {} folders, {} omissions in {:.3}s ({:.0} entries/s)",
            result.items,result.folders,result.omitted,elapsed,result.items as f64/elapsed.max(f64::EPSILON));
        return Ok(());
    }
    let data_dir = if mode.as_deref().is_some_and(|arg| arg == "--data-dir") {
        let path = args.next().map(PathBuf::from).ok_or("Expected a path after --data-dir")?;
        mode = args.next();
        path
    } else {
        util::data_dir()
    };
    if mode.as_deref().is_some_and(|arg| arg == "--install-pack") {
        let pack = match args.next().as_deref().and_then(|arg| arg.to_str()) {
            Some("en") => SemanticPack::EnglishFast,
            Some("multilingual") => SemanticPack::Multilingual,
            _ => {
                eprintln!("Usage: FileInventory --install-pack <en|multilingual>");
                return Ok(());
            }
        };
        let cancel = AtomicBool::new(false);
        let result = semantic::download_pack(&data_dir, pack, &cancel, |_, _, _| {})
            .and_then(|()| Ok(semantic::save_pack_setting(&data_dir, pack)?));
        if let Err(err) = result {
            eprintln!("Could not install {}: {err}", pack.display_name());
        }
        return Ok(());
    }
    let catalog = Catalog::new(data_dir);
    let session = Rc::new(RefCell::new(Some(catalog.start_session()?)));
    let app=AppWindow::new()?;
    ui_state::bind(&app, catalog, session);
    let weak = app.as_weak();
    slint::Timer::single_shot(std::time::Duration::from_millis(100), move || {
        if let Some(app) = weak.upgrade() {
            use slint::ComponentHandle;
            set_titlebar(&app, app.global::<Palette>().get_dark());
        }
    });
    app.run()?;
    Ok(())
}


struct Scratch(PathBuf);
impl Drop for Scratch {
    fn drop(&mut self) { let _=std::fs::remove_dir_all(&self.0); }
}


#[cfg(windows)]
fn set_titlebar(app: &AppWindow, dark: bool) {
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};
    use slint::ComponentHandle;
    use windows_sys::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE};

    let win_handle = app.window().window_handle();
    let Ok(handle) = win_handle.window_handle() else { return; };
    let RawWindowHandle::Win32(handle) = handle.as_raw() else { return; };
    let enabled = i32::from(dark);
    unsafe {
        DwmSetWindowAttribute(handle.hwnd.get() as _, DWMWA_USE_IMMERSIVE_DARK_MODE as u32,
            (&enabled as *const i32).cast(), std::mem::size_of_val(&enabled) as u32);
    }
}

#[cfg(not(windows))]
fn set_titlebar(_app: &AppWindow, _dark: bool) {}
