use std::{collections::BTreeSet, fs, io, path::{Path, PathBuf}, time::Duration};
use chrono::{Local, TimeZone, Utc};
use serde::{Serialize, de::DeserializeOwned};

pub fn data_dir() -> PathBuf {
    dirs::data_local_dir().map(|dir|dir.join("FileInventory")).unwrap_or_else(|| PathBuf::from("."))
}

pub fn now_ms() -> i64 { Utc::now().timestamp_millis() }

pub fn format_local_time(ms: i64) -> String {
    Local.timestamp_millis_opt(ms).single().map(|t| t.format("%Y-%m-%d %H:%M").to_string()).unwrap_or_default()
}

pub fn format_bytes(bytes: u64) -> String {
    let mut value = bytes as f64;
    let mut unit = 0;
    let units = ["B", "KB", "MB", "GB", "TB"];
    while value >= 1024.0 && unit < units.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 { format!("{bytes} B") } else { format!("{value:.1} {}", units[unit]) }
}

pub fn format_time_left(duration: Duration) -> String {
    let secs = duration.as_secs().saturating_add(u64::from(duration.subsec_nanos() != 0));
    match secs {
        3600.. => format!("~{}h {}m left", secs / 3600, secs / 60 % 60),
        60..=3599 => format!("~{}m {}s left", secs / 60, secs % 60),
        _ => format!("~{secs}s left"),
    }
}

pub fn normalize_key(s: &str) -> String { catalog_key(s.trim()) }

// Catalog.Key preserves path whitespace and applies NFC before invariant casing.
pub(crate) fn catalog_key(s: &str) -> String {
    #[cfg(windows)]
    let normalized = {
        use windows_sys::Win32::Globalization::{NormalizeString, NormalizationC};
        let wide: Vec<_> = s.encode_utf16().collect();
        let length = wide.len() as i32;
        // The Windows API reads exactly length units and writes within the supplied buffer.
        let capacity = unsafe { NormalizeString(NormalizationC, wide.as_ptr(), length, std::ptr::null_mut(), 0) };
        let mut output = vec![0; capacity.max(0) as usize];
        let written = if capacity > 0 {
            unsafe { NormalizeString(NormalizationC, wide.as_ptr(), length, output.as_mut_ptr(), capacity) }
        } else { 0 };
        (written > 0).then(|| String::from_utf16_lossy(&output[..written as usize]))
    };
    #[cfg(not(windows))]
    let normalized: Option<String> = None;
    normalized.as_deref().unwrap_or(s).chars().map(|c| {
        if c == 'İ' { c } else { c.to_lowercase().next().unwrap_or(c) }
    }).collect()
}

pub(crate) fn words(s: &str) -> impl Iterator<Item = &str> {
    use std::sync::OnceLock;
    static WORDS: OnceLock<regex::Regex> = OnceLock::new();
    WORDS.get_or_init(|| regex::Regex::new(r"[\p{L}\p{M}\p{N}]+").unwrap())
        .find_iter(s).map(|m| m.as_str())
}

pub fn tokenize_prefixes(name_key: &str) -> Vec<String> { prefixes(name_key, "") }

pub(crate) fn prefixes(name: &str, parent: &str) -> Vec<String> {
    parent.split('\\').chain([name]).flat_map(|segment| {
        std::iter::once(segment).chain(words(segment)).flat_map(|word| {
            word.char_indices().skip(1).map(|(i, _)| i).chain([word.len()])
                .take(2).filter(|&i| i > 0).map(move |i| word[..i].to_owned())
        })
    }).collect::<BTreeSet<_>>().into_iter().collect()
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Theme { #[default] System, Light, Dark, DarkBlue }

pub fn load_theme(dir: &Path) -> Theme {
    match fs::read_to_string(dir.join("theme.txt")).unwrap_or_default().trim() {
        "Light" => Theme::Light, "Dark" => Theme::Dark, "DarkBlue" => Theme::DarkBlue,
        _ => Theme::System,
    }
}

pub fn save_theme(dir: &Path, theme: Theme) -> io::Result<()> {
    fs::create_dir_all(dir)?;
    atomic_write(&dir.join("theme.txt"), |file| {
        use io::Write;
        write!(file, "{theme:?}")
    })
}

pub fn load_selected_drives(dir: &Path) -> Option<Vec<String>> {
    read_json::<Vec<String>>(&dir.join("selected_drives.json")).ok()
}

pub fn save_selected_drives(dir: &Path, ids: Option<&[String]>) -> io::Result<()> {
    let path = dir.join("selected_drives.json");
    match ids {
        Some(ids) => { fs::create_dir_all(dir)?; write_json(&path, &ids) }
        None => match fs::remove_file(path) {
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
            result => result,
        },
    }
}

pub(crate) fn read_json<T: DeserializeOwned>(path: &Path) -> io::Result<T> {
    Ok(serde_json::from_reader(io::BufReader::new(fs::File::open(path)?))?)
}

pub(crate) fn write_json(path: &Path, value: &impl Serialize) -> io::Result<()> {
    atomic_write(path, |file| Ok(serde_json::to_writer_pretty(file, value)?))
}

pub(crate) fn atomic_write<T, E: From<io::Error>>(path: &Path, write: impl FnOnce(&mut fs::File) -> Result<T, E>) -> Result<T, E> {
    let parent = path.parent().filter(|p| !p.as_os_str().is_empty()).unwrap_or(Path::new("."));
    let (temporary, mut file) = ScratchFile::create(parent, ".inventory-", ".tmp")?;
    let result = write(&mut file)?;
    file.sync_all()?;
    drop(file);
    fs::rename(temporary.path(), path)?;
    Ok(result)
}

pub fn copy_to_clipboard(text: &str) -> Result<(), String> {
    arboard::Clipboard::new().and_then(|mut clipboard| clipboard.set_text(text)).map_err(|e| e.to_string())
}

pub fn reveal_in_explorer(path: &str) -> Result<(), String> {
    #[cfg(windows)] {
        use std::os::windows::process::CommandExt;
        if path.contains('"') { return Err("Invalid Explorer path".into()); }
        std::process::Command::new("explorer.exe").raw_arg(format!("/select,\"{path}\""))
            .spawn().map(|_| ()).map_err(|e| e.to_string())
    }
    #[cfg(not(windows))] {
        let _ = path;
        Err("Windows Explorer is unavailable".into())
    }
}

pub fn detect_system_dark_mode() -> bool {
    #[cfg(windows)] {
        use windows_sys::Win32::System::Registry::*;
        let key: Vec<_> = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize".encode_utf16().chain([0]).collect();
        let value: Vec<_> = "AppsUseLightTheme".encode_utf16().chain([0]).collect();
        let mut data = 1u32;
        let mut bytes = 4;
        // RegGetValueW validates the registry type and writes at most four bytes.
        return unsafe {
            RegGetValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr(), RRF_RT_REG_DWORD,
                std::ptr::null_mut(), (&mut data as *mut u32).cast(), &mut bytes) == 0 && data == 0
        };
    }
    #[cfg(not(windows))]
    false
}

pub(crate) fn local_path(mount: &str, rel: &str) -> PathBuf {
    rel.split('\\').fold(PathBuf::from(mount), |path, part| path.join(part))
}

pub(crate) fn parent(rel: &str) -> &str { rel.rsplit_once('\\').map_or("", |(parent, _)| parent) }

pub(crate) fn extension(name: &str, directory: bool) -> String {
    if directory { return String::new(); }
    name.rsplit_once('.').map_or(String::new(), |(_, ext)| catalog_key(ext))
}

pub(crate) fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

pub(crate) fn interrupted() -> io::Error {
    io::Error::new(io::ErrorKind::Interrupted, "Operation cancelled")
}

pub(crate) fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02X}")).collect()
}


pub(crate) struct ScratchFile(PathBuf);
impl ScratchFile {
    pub(crate) fn create(dir: &Path, prefix: &str, suffix: &str) -> io::Result<(Self, fs::File)> {
        loop {
            let path = dir.join(format!("{prefix}{:032x}{suffix}", rand::random::<u128>()));
            match fs::OpenOptions::new().write(true).create_new(true).open(&path) {
                Ok(file) => return Ok((Self(path), file)),
                Err(error) if error.kind()==io::ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(error),
            }
        }
    }

    pub(crate) fn path(&self) -> &Path { &self.0 }
}
impl Drop for ScratchFile {
    fn drop(&mut self) { let _ = fs::remove_file(&self.0); }
}


