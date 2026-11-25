use std::{path::PathBuf,sync::{Arc,atomic::AtomicBool},time::Instant};
use file_inventory::{EngineResult,catalog::{Catalog,Volume},scanner,util};

mod treemap;
mod ui_state;

slint::include_modules!();

fn main() -> EngineResult<()> {
    let mut args=std::env::args_os().skip(1);
    let mode = args.next();
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
        args.next().map(PathBuf::from).ok_or("Expected a path after --data-dir")?
    } else {
        util::data_dir()
    };
    let catalog = Catalog::new(data_dir);
    let _session = catalog.start_session()?;
    let app=AppWindow::new()?;
    ui_state::bind(&app, catalog);
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
    use slint::{ComponentHandle, winit_030::{WinitWindowAccessor, winit::raw_window_handle::{HasWindowHandle, RawWindowHandle}}};
    use windows_sys::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE};

    app.window().with_winit_window(|window| {
        let Ok(handle) = window.window_handle() else { return; };
        let RawWindowHandle::Win32(handle) = handle.as_raw() else { return; };
        let enabled = i32::from(dark);
        unsafe {
            DwmSetWindowAttribute(handle.hwnd.get() as _, DWMWA_USE_IMMERSIVE_DARK_MODE as u32,
                (&enabled as *const i32).cast(), std::mem::size_of_val(&enabled) as u32);
        }
    });
}

#[cfg(not(windows))]
fn set_titlebar(_app: &AppWindow, _dark: bool) {}
