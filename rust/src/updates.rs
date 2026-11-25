use std::time::Duration;
use semver::Version;
use serde::Deserialize;
use crate::EngineResult;

pub const APP_VERSION:&str="1.5.2";
pub const FULL_RELEASE_NOTES:&str="File Inventory 1.5.2
- Restored gold folder icons and added semantic colors for drives, file types, statuses, and actions across all themes.

File Inventory 1.5.1
Local and imported drives are grouped by host. File tables have aligned, sortable columns, and Deep Search refreshes when options change.

File Inventory 1.5.0
Full Rust + Slint native rewrite

Native drive catalog, incremental scans, Deep Search, duplicate verification, and inventory exchange.
Built with Slint (Royalty-Free)";

const REPOSITORY:&str="Caspi7/file-inventory";
const LATEST:&str="https://api.github.com/repos/Caspi7/file-inventory/releases/latest";

#[derive(Clone,Debug)]
pub struct Release {
    pub version:Version,pub download_url:String,pub bytes:u64,pub sha256:String,pub notes:String,
}
#[derive(Deserialize)]
struct GitHubRelease {tag_name:String, draft:bool, body:Option<String>, assets:Vec<Asset>}
#[derive(Deserialize)]
struct Asset {name:String,state:String,size:u64,digest:Option<String>,browser_download_url:String}

pub fn check_for_updates()->EngineResult<Option<Release>> {
    let agent=ureq::AgentBuilder::new().timeout(Duration::from_secs(20)).build();
    let response=match agent.get(LATEST).set("User-Agent",concat!("FileInventory/",env!("CARGO_PKG_VERSION")))
        .set("Accept","application/vnd.github+json").call() {
        Ok(response)=>response,
        Err(ureq::Error::Status(404,_))=>return Ok(None),
        Err(error)=>return Err(error.into()),
    };
    Ok(select(response.into_json()?))
}

fn select(release:GitHubRelease)->Option<Release> {
    if release.draft {return None;}
    let version=Version::parse(release.tag_name.strip_prefix('v')?).ok()?;
    if version<=Version::parse(APP_VERSION).ok()? || !version.build.is_empty() {return None;}
    let installer=format!("FileInventory_Setup_x64_{}.{}.{}.exe",version.major,version.minor,version.patch);
    for asset in release.assets {
        let Some(sha256)=asset.digest.as_deref().and_then(|s|s.strip_prefix("sha256:")) else {continue;};
        if (asset.name==installer || asset.name=="FileInventory.exe") && asset.state=="uploaded"
            && (1..=150_000_000).contains(&asset.size) && sha256.len()==64 && sha256.bytes().all(|b|b.is_ascii_hexdigit())
            && asset.browser_download_url==format!("https://github.com/{REPOSITORY}/releases/download/{}/{}",release.tag_name,asset.name) {
            return Some(Release{version,download_url:asset.browser_download_url,bytes:asset.size,
                sha256:sha256.into(),notes:release.body.unwrap_or_default()});
        }
    }
    None
}

