use std::{
    fs::{self, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::AtomicBool,
    time::{Duration, Instant},
};

use semver::Version;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{scanner::check_cancel, util, EngineResult};

pub const APP_VERSION: &str = "1.6.2";
pub const FULL_RELEASE_NOTES: &str = "File Inventory 1.6.2
- Added a compact close button in the top-right corner of the Deep Search window.
- Advanced Linguistic Search packs are available from GitHub Releases and as optional installer downloads.

File Inventory 1.6.0
- Natural-Language Intent & Facet Chips: Describe what you are looking for in English or Dutch (e.g. \"large pdf invoice from last month without draft\" or \"grote excel vorige maand\") with 1-click dismissible filter chips.
- Path-Aware Main Search: Search keywords now match across folder paths and filenames together (e.g. \"2024 taxes pdf\" finds Taxes\\2024\\return.pdf).
- Sub-Millisecond SymSpell Fuzzy Engine & Bilingual Synonyms: Instant Damerau-Levenshtein typo tolerance, diacritic folding, EN/NL concept expansion, and crisp «word» excerpt highlights.
- Explainable Match Badges: Deep Search results now show sortable Exact, Fuzzy, Close, Filename, Facet, and Concept match badges.
- Optional Linguistic Packs let you find files by meaning offline. Choose English or Multilingual (EN + NL) in Deep Search.

File Inventory 1.5.6
- Added a built-in Help & User Guide with six browsable topics, accessible from the command bar or F1.
- Smart Deep Search understands natural exclusions, slash alternatives, and conversational keywords. It ranks full and partial matches together using keyword coverage, adjacency, filename matches, and text relevance.

File Inventory 1.5.5
- Added 1-click drive removal (trash icon on every drive row in Drive Scope and Drive Library, plus Remove drive button) supporting offline, imported, and online drives, and cleaning up Deep Search content and selected drives.

File Inventory 1.5.4
- Fixed console/terminal window appearing behind File Inventory on Windows startup (restored GUI windows_subsystem).

File Inventory 1.5.3
- Added automatic startup update checks and one-click installation with download progress and SHA-256 verification.

File Inventory 1.5.2
- Restored gold folder icons and added distinct colors for drives, file types, statuses, and actions across all themes.

File Inventory 1.5.1
Local and imported drives are grouped by host. File tables have aligned, sortable columns, and Deep Search refreshes when options change.

File Inventory 1.5.0
Full Rust + Slint native rewrite

Native drive catalog, incremental scans, Deep Search, duplicate verification, and inventory exchange.
Built with Slint (Royalty-Free)";

const REPOSITORY: &str = "Caspi7/file-inventory";
const RELEASES: &str = "https://api.github.com/repos/Caspi7/file-inventory/releases";
const USER_AGENT: &str = concat!("FileInventory/", env!("CARGO_PKG_VERSION"));
const MAX_INSTALLER_BYTES: u64 = 150_000_000;

#[derive(Deserialize, Serialize)]
struct UpdateSettings {
    #[serde(alias = "AutoCheckUpdates", alias = "autoCheckUpdates")]
    auto_update_check: bool,
}

pub fn load_auto_check(data_dir: &Path) -> bool {
    for name in ["update_settings.json", "updates.json"] {
        if let Ok(settings) = util::read_json::<UpdateSettings>(&data_dir.join(name)) {
            return settings.auto_update_check;
        }
    }
    if let Ok(value) = fs::read_to_string(data_dir.join("update-check.txt")) {
        match value.trim().trim_start_matches('\u{feff}').to_ascii_lowercase().as_str() {
            "false" | "off" | "0" => return false,
            "true" | "on" | "1" => return true,
            _ => {}
        }
    }
    true
}

pub fn save_auto_check(data_dir: &Path, enabled: bool) -> EngineResult<()> {
    fs::create_dir_all(data_dir)?;
    util::write_json(&data_dir.join("update_settings.json"), &UpdateSettings {
        auto_update_check: enabled,
    })?;
    Ok(())
}

#[derive(Clone, Debug)]
pub struct Release {
    pub version: Version,
    pub download_url: String,
    pub bytes: u64,
    pub sha256: String,
    pub notes: String,
}

impl Release {
    pub fn page_url(&self) -> String {
        format!("https://github.com/{REPOSITORY}/releases/tag/v{}", self.version)
    }
}

#[derive(Deserialize)]
struct GitHubRelease {
    tag_name: String,
    draft: bool,
    body: Option<String>,
    assets: Vec<Asset>,
}

#[derive(Deserialize)]
struct Asset {
    name: String,
    state: String,
    size: u64,
    digest: Option<String>,
    browser_download_url: String,
}

pub fn check_for_updates() -> EngineResult<Option<Release>> {
    let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(20)).build();
    let get = |url: &str| {
        agent.get(url).set("User-Agent", USER_AGENT).set("Accept", "application/vnd.github+json").call()
    };
    let releases = match get(&format!("{RELEASES}?per_page=15")) {
        Ok(response) => response.into_json()?,
        Err(ureq::Error::Status(404, _)) => match get(&format!("{RELEASES}/latest")) {
            Ok(response) => vec![response.into_json()?],
            Err(ureq::Error::Status(404, _)) => Vec::new(),
            Err(error) => return Err(error.into()),
        },
        Err(error) => return Err(error.into()),
    };
    Ok(select(releases))
}

fn select(releases: Vec<GitHubRelease>) -> Option<Release> {
    let installed = Version::parse(APP_VERSION).ok()?;
    releases.into_iter().filter_map(|release| {
        if release.draft {
            return None;
        }
        let version = Version::parse(release.tag_name.strip_prefix('v')?).ok()?;
        if version <= installed || !version.pre.is_empty() || !version.build.is_empty() {
            return None;
        }
        let installer = format!("FileInventory_Setup_x64_{version}.exe");
        let url = format!("https://github.com/{REPOSITORY}/releases/download/{}/{installer}", release.tag_name);
        for asset in release.assets {
            let Some(sha256) = asset.digest.as_deref().and_then(|value| value.strip_prefix("sha256:")) else {
                continue;
            };
            if asset.name == installer && asset.state == "uploaded" && asset.browser_download_url == url
                && (1..=MAX_INSTALLER_BYTES).contains(&asset.size)
                && sha256.len() == 64 && sha256.bytes().all(|byte| byte.is_ascii_hexdigit())
            {
                return Some(Release {
                    version, download_url: url, bytes: asset.size,
                    sha256: sha256.to_ascii_lowercase(), notes: release.body.unwrap_or_default(),
                });
            }
        }
        None
    }).max_by(|left, right| left.version.cmp(&right.version))
}

pub fn download_installer(
    release: &Release,
    cancel: &AtomicBool,
    mut on_progress: impl FnMut(u64, u64),
) -> EngineResult<PathBuf> {
    check_cancel(cancel)?;
    let agent = ureq::AgentBuilder::new().timeout_connect(Duration::from_secs(10))
        .timeout_read(Duration::from_secs(10)).build();
    let mut input = agent.get(&release.download_url).set("User-Agent", USER_AGENT).call()?.into_reader();
    check_cancel(cancel)?;

    let path = std::env::temp_dir().join(format!("FileInventory-update-{:032x}.exe", rand::random::<u128>()));
    let mut output = OpenOptions::new().write(true).create_new(true).open(&path)?;
    #[cfg(windows)]
    let installer_path = &path;
    let result = (move || -> EngineResult<()> {
        let mut hash = Sha256::new();
        let mut buffer = [0; 81920];
        let mut downloaded = 0;
        let mut sample_at = Instant::now();
        loop {
            check_cancel(cancel)?;
            let count = input.read(&mut buffer)?;
            check_cancel(cancel)?;
            if count == 0 {
                break;
            }
            downloaded += count as u64;
            if downloaded > release.bytes {
                return Err(util::invalid("The installer size did not match the release.").into());
            }
            hash.update(&buffer[..count]);
            output.write_all(&buffer[..count])?;
            if downloaded < release.bytes && sample_at.elapsed() >= Duration::from_millis(100) {
                on_progress(downloaded, release.bytes);
                sample_at = Instant::now();
            }
        }
        if downloaded != release.bytes {
            return Err(util::invalid("The installer size did not match the release.").into());
        }
        if !util::hex(&hash.finalize()).eq_ignore_ascii_case(&release.sha256) {
            return Err(util::invalid("The installer did not match the GitHub release checksum.").into());
        }
        output.sync_all()?;
        drop(output);

        #[cfg(windows)]
        {
            let mut zone_path = installer_path.as_os_str().to_os_string();
            zone_path.push(":Zone.Identifier");
            fs::write(zone_path, format!("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={}\r\n", release.download_url))?;
        }
        on_progress(downloaded, release.bytes);
        check_cancel(cancel)?;
        Ok(())
    })();
    if let Err(error) = result {
        let _ = fs::remove_file(&path);
        return Err(error);
    }
    Ok(path)
}

pub fn launch_installer(installer_path: &Path) -> EngineResult<()> {
    Command::new(installer_path).args(["/SP-", "/SILENT", "/NORESTART", "/UPDATE"]).spawn()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn release(tag: &str, draft: bool, filename: &str, digest: String) -> GitHubRelease {
        GitHubRelease {
            tag_name: tag.into(), draft, body: Some("Changes".into()),
            assets: vec![Asset {
                name: filename.into(), state: "uploaded".into(), size: 100,
                digest: Some(digest),
                browser_download_url: format!("https://github.com/{REPOSITORY}/releases/download/{tag}/{filename}"),
            }],
        }
    }

    #[test]
    fn selects_highest_newer_release_with_verified_installer_metadata() {
        let valid = |version: &str, draft| {
            release(&format!("v{version}"), draft, &format!("FileInventory_Setup_x64_{version}.exe"),
                format!("sha256:{}", "AB".repeat(32)))
        };
        let invalid = vec![
            valid(APP_VERSION, false),
            valid("1.5.2", false),
            valid("9.0.0", true),
            release("v9.0.1", false, "FileInventory_Setup_x64_9.0.1.exe", "sha256:bad".into()),
            release("v9.0.2", false, "FileInventory.exe", format!("sha256:{}", "ab".repeat(32))),
        ];
        assert!(select(invalid).is_none());
        let selected = select(vec![valid("1.7.0", false), valid("1.5.4", false), valid("9.0.0", true)]).unwrap();
        assert_eq!(selected.version, Version::new(1, 7, 0));
        assert_eq!(selected.sha256, "ab".repeat(32));
    }

    #[test]
    fn auto_check_settings_round_trip_and_read_legacy_values() -> EngineResult<()> {
        let dir = std::env::temp_dir();
        let (scratch, file) = util::ScratchFile::create(&dir, "file-inventory-settings-", ".tmp")?;
        drop(file);
        // Use a unique directory and remove only the settings files created by this test.
        let data_dir = scratch.path().with_extension("dir");
        fs::create_dir(&data_dir)?;
        let result = (|| -> EngineResult<()> {
            assert!(load_auto_check(&data_dir));
            fs::write(data_dir.join("update-check.txt"), "False\r\n")?;
            assert!(!load_auto_check(&data_dir));
            fs::write(data_dir.join("updates.json"), r#"{"AutoCheckUpdates":true}"#)?;
            assert!(load_auto_check(&data_dir));
            save_auto_check(&data_dir, false)?;
            assert!(!load_auto_check(&data_dir));
            save_auto_check(&data_dir, true)?;
            assert!(load_auto_check(&data_dir));
            Ok(())
        })();
        for name in ["update-check.txt", "updates.json", "update_settings.json"] {
            let _ = fs::remove_file(data_dir.join(name));
        }
        fs::remove_dir(data_dir)?;
        result
    }
}
