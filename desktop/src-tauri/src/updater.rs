use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{collections::HashSet, fs, io::{Read, Write}, path::{Component, Path, PathBuf}, process::Command, time::{Duration, SystemTime, UNIX_EPOCH}};
use tauri::{Emitter, Manager};

const REPO: &str = "HikiTanis/ai-conversation-manager";
const EXE: &str = "conversation-manager-desktop.exe";
const MAX_PACKAGE: u64 = 512 * 1024 * 1024;
const ROOTS: &[&str] = &[EXE, "engine", "Start.cmd", "README.txt", "LICENSE.txt"];

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    current_version: String,
    version: String,
    available: bool,
    notes: String,
    published_at: String,
    installed: bool,
}

#[derive(Deserialize)]
struct Release { tag_name: String, draft: bool, prerelease: bool, #[serde(default)] body: Option<String>, #[serde(default)] published_at: Option<String>, assets: Vec<Asset> }
#[derive(Deserialize)]
struct Asset { name: String, browser_download_url: String, size: u64, digest: Option<String> }
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Progress { phase: &'static str, downloaded: u64, total: u64 }

fn client() -> Result<Client, String> {
    Client::builder().user_agent(concat!("AI-Conversation-Manager/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15)).timeout(Duration::from_secs(300))
        .redirect(reqwest::redirect::Policy::custom(|attempt| {
            if attempt.previous().len() >= 5 { return attempt.error("Too many redirects"); }
            let url = attempt.url();
            if url.scheme() == "https" && matches!(url.host_str(), Some("github.com" | "api.github.com" | "release-assets.githubusercontent.com" | "objects.githubusercontent.com")) { attempt.follow() }
            else { attempt.error("Untrusted update redirect") }
        })).build().map_err(|e| e.to_string())
}

fn version(tag: &str) -> Result<semver::Version, String> {
    let v = semver::Version::parse(tag.strip_prefix('v').unwrap_or(tag)).map_err(|_| "Invalid release version")?;
    if !v.pre.is_empty() || !v.build.is_empty() { return Err("Only stable releases are supported".into()); }
    Ok(v)
}

fn latest_api(client: &Client) -> Result<Release, String> {
    let response = client.get(format!("https://api.github.com/repos/{REPO}/releases/latest"))
        .timeout(Duration::from_secs(20)).header("Accept", "application/vnd.github+json").send().map_err(|e| e.to_string())?
        .error_for_status().map_err(|e| format!("Cannot check GitHub releases: {e}"))?;
    let mut bytes = Vec::new();
    response.take(2 * 1024 * 1024 + 1).read_to_end(&mut bytes).map_err(|e| e.to_string())?;
    if bytes.len() > 2 * 1024 * 1024 { return Err("Release response is too large".into()); }
    let release: Release = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
    if release.draft || release.prerelease { return Err("Not a stable release".into()); }
    version(&release.tag_name)?;
    Ok(release)
}

fn read_bounded(response: reqwest::blocking::Response, limit: u64) -> Result<Vec<u8>, String> {
    let mut bytes = Vec::new();
    response.error_for_status().map_err(|e| e.to_string())?.take(limit + 1).read_to_end(&mut bytes).map_err(|e| e.to_string())?;
    if bytes.len() as u64 > limit { return Err("Update metadata is too large".into()); }
    Ok(bytes)
}

fn latest_manifest(client: &Client) -> Result<Release, String> {
    let response = client.get(format!("https://github.com/{REPO}/releases/latest/download/update.json"))
        .timeout(Duration::from_secs(20)).send().map_err(|e| e.to_string())?;
    let release: Release = serde_json::from_slice(&read_bounded(response, 2 * 1024 * 1024)?).map_err(|e| e.to_string())?;
    if release.draft || release.prerelease { return Err("Not a stable release".into()); }
    version(&release.tag_name)?;
    select_asset(&release, false)?; select_asset(&release, true)?;
    Ok(release)
}

fn release_tag(url: &reqwest::Url) -> Result<String, String> {
    let prefix = format!("/{REPO}/releases/tag/");
    if url.scheme() != "https" || url.host_str() != Some("github.com") { return Err("Invalid release page source".into()); }
    let tag = url.path().strip_prefix(&prefix).ok_or("Invalid release page URL")?;
    if tag != format!("v{}", version(tag)?) { return Err("Invalid stable release tag".into()); }
    Ok(tag.into())
}

fn checksum(text: &str, name: &str) -> Result<String, String> {
    let mut hashes = Vec::new();
    for line in text.lines() {
        let parts: Vec<_> = line.split_whitespace().collect();
        if parts.len() == 2 && parts[1] == name { hashes.push(parts[0]); }
    }
    if hashes.len() != 1 || hashes[0].len() != 64 || !hashes[0].bytes().all(|b| b.is_ascii_hexdigit()) { return Err("Missing or invalid release checksum".into()); }
    Ok(format!("sha256:{}", hashes[0].to_ascii_lowercase()))
}

// Pre-updater releases have no update.json. GitHub's public latest redirect and
// checksum attachment work without an API token or an API rate-limit budget.
fn latest_public_page(client: &Client) -> Result<Release, String> {
    let response = client.get(format!("https://github.com/{REPO}/releases/latest"))
        .timeout(Duration::from_secs(20)).send().map_err(|e| format!("{e:?}"))?.error_for_status().map_err(|e| e.to_string())?;
    let tag = release_tag(response.url())?;
    drop(response);
    let base = format!("https://github.com/{REPO}/releases/download/{tag}");
    let response = client.get(format!("{base}/SHA256SUMS.txt")).timeout(Duration::from_secs(20)).send().map_err(|e| e.to_string())?;
    let checks = String::from_utf8(read_bounded(response, 16384)?).map_err(|e| e.to_string())?;
    let mut assets = Vec::new();
    for suffix in ["Portable.zip", "Setup.exe"] {
        let name = format!("AI-Conversation-Manager-{}-Windows-x64-{suffix}", version(&tag)?);
        let digest = checksum(&checks, &name)?;
        let url = format!("{base}/{name}");
        let response = client.head(&url).timeout(Duration::from_secs(20)).send().map_err(|e| e.to_string())?.error_for_status().map_err(|e| e.to_string())?;
        let size = response.headers().get(reqwest::header::CONTENT_LENGTH).and_then(|s| s.to_str().ok()).and_then(|s| s.parse::<u64>().ok()).ok_or("Package size is unavailable")?;
        assets.push(Asset { name, browser_download_url: url, size, digest: Some(digest) });
    }
    let notes = client.get(format!("https://raw.githubusercontent.com/{REPO}/{tag}/docs/releases/{tag}.md"))
        .timeout(Duration::from_secs(10)).send().map_err(|e| e.to_string()).and_then(|r| read_bounded(r, 128 * 1024))
        .ok().and_then(|b| String::from_utf8(b).ok());
    let release = Release { tag_name: tag, draft: false, prerelease: false, body: notes, published_at: None, assets };
    select_asset(&release, false)?; select_asset(&release, true)?;
    Ok(release)
}

fn latest(client: &Client) -> Result<Release, String> {
    latest_manifest(client).or_else(|manifest_error| latest_api(client).or_else(|api_error| {
        latest_public_page(client).or_else(|_| latest_public_page(client)).map_err(|page_error| format!("无法连接更新源 / Update sources unavailable.\nRelease manifest: {manifest_error}\nGitHub API: {api_error}\nPublic release: {page_error}"))
    }))
}

fn app_directory() -> Result<PathBuf, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    Ok(exe.parent().ok_or("Missing application folder")?.to_path_buf())
}

#[tauri::command]
pub async fn check_update() -> Result<UpdateInfo, String> {
    tauri::async_runtime::spawn_blocking(|| {
        let release = latest(&client()?)?;
        let current = env!("CARGO_PKG_VERSION");
        let installed = app_directory()?.join("uninstall.exe").is_file();
        let available = version(&release.tag_name)? > version(current)?;
        if available { select_asset(&release, installed)?; }
        Ok(UpdateInfo { current_version: current.into(), version: version(&release.tag_name)?.to_string(), available,
            notes: release.body.unwrap_or_default(), published_at: release.published_at.unwrap_or_default(), installed })
    }).await.map_err(|e| e.to_string())?
}

fn select_asset(release: &Release, installed: bool) -> Result<&Asset, String> {
    let name = format!("AI-Conversation-Manager-{}-Windows-x64-{}", version(&release.tag_name)?, if installed { "Setup.exe" } else { "Portable.zip" });
    let matches: Vec<_> = release.assets.iter().filter(|a| a.name == name).collect();
    if matches.len() != 1 { return Err("This release does not yet contain the required Windows package. Try again later.".into()); }
    let asset = matches[0];
    let expected = format!("https://github.com/{REPO}/releases/download/{}/{}", release.tag_name, name);
    if asset.browser_download_url != expected || asset.size == 0 || asset.size > MAX_PACKAGE { return Err("Invalid update package metadata".into()); }
    expected_hash(asset)?;
    Ok(asset)
}

fn expected_hash(asset: &Asset) -> Result<String, String> {
    let hash = asset.digest.as_deref().and_then(|s| s.strip_prefix("sha256:")).ok_or("Release is missing its SHA-256 digest")?;
    if hash.len() != 64 || !hash.bytes().all(|b| b.is_ascii_hexdigit()) { return Err("Invalid package SHA-256 digest".into()); }
    Ok(hash.to_ascii_lowercase())
}

fn sha256(path: &Path) -> Result<String, String> {
    let mut file = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut hash = Sha256::new(); let mut buffer = [0; 65536];
    loop { let n = file.read(&mut buffer).map_err(|e| e.to_string())?; if n == 0 { break; } hash.update(&buffer[..n]); }
    Ok(format!("{:x}", hash.finalize()))
}

fn report(app: &tauri::AppHandle, phase: &'static str, downloaded: u64, total: u64) { let _ = app.emit("update-progress", Progress { phase, downloaded, total }); }

fn download(client: &Client, asset: &Asset, target: &Path, progress: impl Fn(u64)) -> Result<(), String> {
    let mut response = client.get(&asset.browser_download_url).send().map_err(|e| e.to_string())?.error_for_status().map_err(|e| e.to_string())?;
    let mut file = fs::OpenOptions::new().write(true).create_new(true).open(target).map_err(|e| e.to_string())?;
    let mut count = 0; let mut buffer = [0; 65536];
    loop {
        let n = response.read(&mut buffer).map_err(|e| e.to_string())?; if n == 0 { break; }
        count += n as u64; if count > asset.size { return Err("Update download exceeded its expected size".into()); }
        file.write_all(&buffer[..n]).map_err(|e| e.to_string())?; progress(count);
    }
    file.sync_all().map_err(|e| e.to_string())?; drop(file);
    if count != asset.size || sha256(target)? != expected_hash(asset)? { return Err("Update integrity check failed. The current application has not been changed.".into()); }
    Ok(())
}

#[derive(Deserialize, Serialize)]
struct FileEntry { path: String, sha256: String }
#[derive(Deserialize, Serialize)]
struct Handoff { parent: u32, target: PathBuf, installed: bool, package_hash: String, files: Vec<FileEntry> }

// All ZIP members must remain inside the published top-level directory. Only
// application-owned roots are replaced; user files alongside the app survive.
fn unpack(package: &Path, stage: &Path, release_version: &str) -> Result<Vec<FileEntry>, String> {
    let mut zip = zip::ZipArchive::new(fs::File::open(package).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
    if zip.len() > 10000 { return Err("Too many update files".into()); }
    let top = format!("AI-Conversation-Manager-{release_version}-Windows-x64");
    let mut files = Vec::new(); let mut seen = HashSet::new(); let mut total = 0u64;
    for i in 0..zip.len() {
        let mut entry = zip.by_index(i).map_err(|e| e.to_string())?;
        let name = entry.name().replace('\\', "/");
        let relative = name.strip_prefix(&(top.clone() + "/")).ok_or("Invalid package root")?.trim_end_matches('/');
        if relative.is_empty() { continue; }
        validate_member(relative)?;
        if entry.unix_mode().map(|m| m & 0o170000 == 0o120000).unwrap_or(false) { return Err("Links are not allowed in updates".into()); }
        let destination = stage.join(relative);
        if entry.is_dir() { fs::create_dir_all(&destination).map_err(|e| e.to_string())?; continue; }
        if !seen.insert(relative.to_ascii_lowercase()) { return Err("Duplicate update file".into()); }
        total = total.checked_add(entry.size()).ok_or("Package too large")?;
        if total > 1024 * 1024 * 1024 { return Err("Unpacked update is too large".into()); }
        fs::create_dir_all(destination.parent().ok_or("Invalid file path")?).map_err(|e| e.to_string())?;
        let mut out = fs::OpenOptions::new().write(true).create_new(true).open(&destination).map_err(|e| e.to_string())?;
        let written = std::io::copy(&mut (&mut entry).take(1024 * 1024 * 1024 + 1), &mut out).map_err(|e| e.to_string())?;
        if written != entry.size() { return Err("Invalid unpacked file size".into()); }
        out.sync_all().map_err(|e| e.to_string())?; drop(out);
        files.push(FileEntry { path: relative.into(), sha256: sha256(&destination)? });
    }
    for required in [EXE, "engine/CodexConversationManager.exe", "engine/CodexConversationManager.exe.config", "engine/backend.cjs", "engine/packages.cjs", "engine/runtime/node.exe"] {
        if !seen.contains(required.to_ascii_lowercase().as_str()) { return Err(format!("Update is missing {required}")); }
    }
    Ok(files)
}

fn validate_member(name: &str) -> Result<(), String> {
    if name.is_empty() || name.contains('\\') || name.contains(':') || !Path::new(name).components().all(|c| matches!(c, Component::Normal(_))) { return Err("Unsafe update path".into()); }
    for component in name.split('/') {
        let base = component.split('.').next().unwrap_or("").to_ascii_uppercase();
        if component.is_empty() || component.ends_with(['.', ' ']) || matches!(base.as_str(), "CON" | "PRN" | "AUX" | "NUL") ||
            (base.len() == 4 && (base.starts_with("COM") || base.starts_with("LPT")) && base.as_bytes()[3].is_ascii_digit()) { return Err("Unsafe Windows filename".into()); }
    }
    let root = name.split('/').next().unwrap_or("");
    if !ROOTS.contains(&root) || (root != "engine" && name.contains('/')) { return Err("Unexpected update file".into()); }
    Ok(())
}

fn ensure_not_link(path: &Path) -> Result<(), String> {
    if let Ok(meta) = fs::symlink_metadata(path) {
        #[cfg(windows)] { use std::os::windows::fs::MetadataExt; if meta.file_attributes() & 0x400 != 0 { return Err("Update target contains a junction or link".into()); } }
        if meta.file_type().is_symlink() { return Err("Update target contains a link".into()); }
    }
    Ok(())
}

#[tauri::command]
pub async fn install_update(app: tauri::AppHandle, expected_version: String) -> Result<(), String> {
    if cfg!(debug_assertions) { return Err("Updates can only be installed by a packaged release build".into()); }
    if super::OPERATING.swap(true, std::sync::atomic::Ordering::SeqCst) { return Err("Another operation is running. Wait for it to finish before updating.".into()); }
    let guard = super::OperationGuard;
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = guard;
        let client = client()?;
        let release = latest(&client)?;
        let release_version = version(&release.tag_name)?.to_string();
        if release_version != expected_version || version(&release_version)? <= version(env!("CARGO_PKG_VERSION"))? { return Err("Release changed. Check for updates again.".into()); }
        let target = app_directory()?;
        for parent in target.ancestors() { ensure_not_link(parent)?; }
        for root in ROOTS { ensure_not_link(&target.join(root))?; }
        // Verify write access before downloading or closing the application.
        let probe = target.join(format!(".update-write-test-{}", std::process::id()));
        fs::OpenOptions::new().write(true).create_new(true).open(&probe).map_err(|e| format!("Application folder is not writable: {e}"))?;
        fs::remove_file(&probe).map_err(|e| e.to_string())?;
        let installed = target.join("uninstall.exe").is_file();
        let asset = select_asset(&release, installed)?;
        let cache = app.path().app_local_data_dir().map_err(|e| e.to_string())?.join("updates");
        fs::create_dir_all(&cache).map_err(|e| e.to_string())?;
        let work = cache.join(format!("{}-{}-{}", release_version, std::process::id(), SystemTime::now().duration_since(UNIX_EPOCH).map_err(|e| e.to_string())?.as_millis()));
        fs::create_dir(&work).map_err(|e| e.to_string())?;
        let result = (|| {
            let package = work.join(if installed { "setup.exe" } else { "package.zip" });
            report(&app, "downloading", 0, asset.size);
            download(&client, asset, &package, |n| report(&app, "downloading", n, asset.size))?;
            report(&app, "verifying", asset.size, asset.size);
            let stage = work.join("payload"); fs::create_dir(&stage).map_err(|e| e.to_string())?;
            let files = if installed { Vec::new() } else { unpack(&package, &stage, &release_version)? };
            let handoff = Handoff { parent: std::process::id(), target, installed, package_hash: expected_hash(asset)?, files };
            fs::write(work.join("handoff.json"), serde_json::to_vec(&handoff).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
            fs::copy(std::env::current_exe().map_err(|e| e.to_string())?, work.join("update-helper.exe")).map_err(|e| e.to_string())?;
            let mut command = Command::new(work.join("update-helper.exe")); command.arg("--apply-update").current_dir(&work);
            #[cfg(windows)] { use std::os::windows::process::CommandExt; command.creation_flags(0x08000000); }
            command.spawn().map_err(|e| format!("Cannot start updater: {e}"))?;
            // The helper validates the handoff before the main application exits.
            for _ in 0..100 {
                if work.join("ready").is_file() { report(&app, "installing", asset.size, asset.size); app.exit(0); return Ok(()); }
                if work.join("error.txt").is_file() { return Err(fs::read_to_string(work.join("error.txt")).unwrap_or_else(|_| "Updater failed".into())); }
                std::thread::sleep(Duration::from_millis(100));
            }
            // No ready acknowledgement: do not let a delayed helper install later.
            fs::write(work.join("cancel"), b"cancelled").map_err(|e| e.to_string())?;
            Err("Updater did not start. The application remains open.".into())
        })();
        // Keep failed handoffs for diagnostics; nothing in the app was changed.
        result
    }).await.map_err(|e| e.to_string())?
}

fn verify_handoff(work: &Path, h: &Handoff) -> Result<(), String> {
    if !h.target.is_absolute() || !h.target.join(EXE).is_file() || h.parent == std::process::id() { return Err("Invalid update destination".into()); }
    for path in h.target.ancestors() { ensure_not_link(path)?; }
    for root in ROOTS { ensure_not_link(&h.target.join(root))?; }
    if h.installed {
        if sha256(&work.join("setup.exe"))? != h.package_hash { return Err("Installer hash changed".into()); }
    } else {
        let mut seen = HashSet::new();
        for entry in &h.files {
            validate_member(&entry.path)?;
            if !seen.insert(entry.path.to_ascii_lowercase()) || sha256(&work.join("payload").join(&entry.path))? != entry.sha256 { return Err("Staged update changed".into()); }
        }
        if !seen.contains(EXE) || !seen.contains("engine/codexconversationmanager.exe") { return Err("Incomplete update".into()); }
    }
    Ok(())
}

// Rename only application-owned roots on the same volume. A failed replacement
// rolls every installed root back before restarting the old version.
fn replace_portable(target: &Path, stage: &Path, backup: &Path, fail_after: Option<usize>) -> Result<(), String> {
    fs::create_dir(backup).map_err(|e| e.to_string())?;
    let mut moved = Vec::new(); let mut installed = Vec::new();
    let result = (|| {
        for root in ROOTS {
            if !stage.join(root).exists() { continue; }
            ensure_not_link(&target.join(root))?;
            if target.join(root).exists() { fs::rename(target.join(root), backup.join(root)).map_err(|e| e.to_string())?; moved.push(*root); }
            fs::rename(stage.join(root), target.join(root)).map_err(|e| e.to_string())?; installed.push(*root);
            if fail_after == Some(installed.len()) { return Err("Injected replacement failure".into()); }
        }
        Ok(())
    })();
    if let Err(error) = result {
        let mut errors = vec![error];
        for root in installed.iter().rev() { if let Err(e) = fs::rename(target.join(root), stage.join(root)) { errors.push(e.to_string()); } }
        for root in moved.iter().rev() { if let Err(e) = fs::rename(backup.join(root), target.join(root)) { errors.push(e.to_string()); } }
        return Err(format!("Update failed; rollback attempted. {}. Backup: {}", errors.join("; "), backup.display()));
    }
    Ok(())
}

#[cfg(windows)]
fn wait_for_parent(pid: u32) -> Result<(), String> {
    unsafe extern "system" { fn OpenProcess(access: u32, inherit: i32, pid: u32) -> *mut std::ffi::c_void; fn WaitForSingleObject(handle: *mut std::ffi::c_void, milliseconds: u32) -> u32; fn CloseHandle(handle: *mut std::ffi::c_void) -> i32; }
    unsafe { let handle = OpenProcess(0x00100000, 0, pid); if handle.is_null() { return Ok(()); }
        let result = WaitForSingleObject(handle, 120000); CloseHandle(handle);
        if result == 0 { Ok(()) } else { Err("Application did not exit. Update cancelled.".into()) }
    }
}
#[cfg(not(windows))]
fn wait_for_parent(_: u32) -> Result<(), String> { Err("Windows updater only".into()) }

fn apply(work: &Path) -> Result<(), String> {
    let h: Handoff = serde_json::from_slice(&fs::read(work.join("handoff.json")).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
    verify_handoff(work, &h)?;
    fs::write(work.join("ready"), b"ready").map_err(|e| e.to_string())?;
    wait_for_parent(h.parent)?;
    if work.join("cancel").exists() { return Err("Update cancelled before application exit".into()); }
    verify_handoff(work, &h)?;
    if h.installed {
        let status = Command::new(work.join("setup.exe")).args(["/S", "/UPDATE", "/R"]).arg(format!("/D={}", h.target.display())).status().map_err(|e| e.to_string())?;
        if !status.success() { return Err(format!("Installer exited with {status}. Run the downloaded installer again: {}", work.join("setup.exe").display())); }
    } else {
        // Staging beside the target makes every commit/rollback rename same-volume.
        let stage = h.target.join(format!(".update-stage-{}", h.parent));
        fs::create_dir(&stage).map_err(|e| e.to_string())?;
        for entry in &h.files {
            let out = stage.join(&entry.path); fs::create_dir_all(out.parent().ok_or("Invalid update path")?).map_err(|e| e.to_string())?;
            fs::copy(work.join("payload").join(&entry.path), &out).map_err(|e| e.to_string())?;
            if sha256(&out)? != entry.sha256 { return Err("Staging integrity check failed".into()); }
        }
        let backup = h.target.join(format!(".update-backup-{}", h.parent));
        let result = replace_portable(&h.target, &stage, &backup, None);
        let restart = Command::new(h.target.join(EXE)).current_dir(&h.target).spawn().map_err(|e| e.to_string());
        result?; restart?;
        // Keep one recovery copy for inspection; it is never mixed with live files.
        let _ = fs::remove_dir(&stage);
    }
    let _ = fs::remove_file(work.join("package.zip"));
    let _ = fs::remove_dir_all(work.join("payload"));
    Ok(())
}

pub fn run_helper() -> bool {
    if std::env::args().nth(1).as_deref() != Some("--apply-update") { return false; }
    let work = match std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)) { Some(p) => p, None => return true };
    if let Err(error) = apply(&work) {
        let _ = fs::write(work.join("error.txt"), &error);
        #[cfg(windows)] unsafe {
            #[link(name = "user32")] unsafe extern "system" { fn MessageBoxW(hwnd: *mut std::ffi::c_void, text: *const u16, caption: *const u16, flags: u32) -> i32; }
            let message: Vec<_> = format!("更新未完成 / Update failed\n{error}\n\n{}", work.display()).encode_utf16().chain(Some(0)).collect();
            let title: Vec<_> = "AI Conversation Manager".encode_utf16().chain(Some(0)).collect();
            MessageBoxW(std::ptr::null_mut(), message.as_ptr(), title.as_ptr(), 0x10);
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    fn test_root(name: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!("ccm-{name}-{}-{}", std::process::id(), SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos()));
        fs::create_dir_all(&root).unwrap(); root
    }
    #[test] fn zip_rejects_traversal_and_missing_engine() {
        let root = test_root("zip");
        for (index, member) in ["AI-Conversation-Manager-2.1.0-Windows-x64/engine/../../outside.txt", "AI-Conversation-Manager-2.1.0-Windows-x64/conversation-manager-desktop.exe"].iter().enumerate() {
            let package = root.join(format!("{index}.zip"));
            let mut writer = zip::ZipWriter::new(fs::File::create(&package).unwrap());
            writer.start_file(*member, zip::write::SimpleFileOptions::default()).unwrap(); writer.write_all(b"test").unwrap(); writer.finish().unwrap();
            assert!(unpack(&package, &root.join(format!("stage-{index}")), "2.1.0").is_err());
        }
        assert!(!root.join("outside.txt").exists()); fs::remove_dir_all(root).unwrap();
    }
    #[test] fn tampered_staging_is_rejected_before_replacement() {
        let root = test_root("tamper"); let target = root.join("app"); let payload = root.join("payload");
        fs::create_dir(&target).unwrap(); fs::create_dir(&payload).unwrap();
        fs::write(target.join(EXE), b"old").unwrap(); fs::write(payload.join(EXE), b"new").unwrap();
        let entry = FileEntry { path: EXE.into(), sha256: sha256(&payload.join(EXE)).unwrap() };
        fs::write(payload.join(EXE), b"tampered").unwrap();
        let h = Handoff { parent: 0, target: target.clone(), installed: false, package_hash: String::new(), files: vec![entry] };
        assert!(verify_handoff(&root, &h).is_err()); assert_eq!(fs::read(target.join(EXE)).unwrap(), b"old");
        fs::remove_dir_all(root).unwrap();
    }
    #[test] #[ignore = "read-only network integration against the public release API"]
    fn live_release_metadata_is_compatible() {
        let release = latest(&client().unwrap()).unwrap();
        assert!(select_asset(&release, false).is_ok()); assert!(select_asset(&release, true).is_ok());
    }
    #[test] #[ignore = "read-only network integration that deliberately avoids the GitHub API"]
    fn live_public_fallback_works_without_api() {
        let release = latest_public_page(&client().unwrap()).unwrap();
        assert!(select_asset(&release, false).is_ok()); assert!(select_asset(&release, true).is_ok());
    }
    #[test] fn public_fallback_validates_tag_and_checksums() {
        assert!(release_tag(&reqwest::Url::parse(&format!("https://github.com/{REPO}/releases/tag/v2.1.0")).unwrap()).is_ok());
        for url in ["https://evil.example/releases/tag/v2.1.0", "https://github.com/other/repo/releases/tag/v2.1.0", "https://github.com/HikiTanis/ai-conversation-manager/releases/tag/v2.2.0-rc.1"] { assert!(release_tag(&reqwest::Url::parse(url).unwrap()).is_err()); }
        let line = format!("{}  app.zip\n", "a".repeat(64));
        assert!(checksum(&line, "app.zip").is_ok()); assert!(checksum(&line, "wrong.zip").is_err()); assert!(checksum(&(line.clone() + &line), "app.zip").is_err());
    }
    #[test] fn versions_are_numeric_and_stable() {
        assert!(version("v2.10.0").unwrap() > version("2.9.0").unwrap());
        assert!(version("v2.1.0-rc.1").is_err()); assert!(version("latest").is_err());
    }
    #[test] fn paths_cannot_escape_or_overwrite_user_data() {
        for name in ["../config.toml", "engine/../../x", "/engine/x", "engine/x:y", "engine/CON.txt", "engine/trailing.", "config.toml", "Start.cmd/child"] { assert!(validate_member(name).is_err(), "{name}"); }
        assert!(validate_member("engine/runtime/node.exe").is_ok());
    }
    #[test] fn releases_require_exact_asset_and_hash() {
        let mut release = Release { tag_name: "v2.1.0".into(), draft: false, prerelease: false, body: None, published_at: None, assets: vec![Asset { name: "AI-Conversation-Manager-2.1.0-Windows-x64-Portable.zip".into(), browser_download_url: format!("https://github.com/{REPO}/releases/download/v2.1.0/AI-Conversation-Manager-2.1.0-Windows-x64-Portable.zip"), size: 100, digest: Some(format!("sha256:{}", "a".repeat(64))) }] };
        assert!(select_asset(&release, false).is_ok()); assert!(select_asset(&release, true).is_err());
        release.assets[0].browser_download_url = "https://example.com/evil.exe".into(); assert!(select_asset(&release, false).is_err());
        release.assets[0].digest = None; assert!(expected_hash(&release.assets[0]).is_err());
    }
    #[test] fn rollback_preserves_old_app_and_unmanaged_files() {
        let root = std::env::temp_dir().join(format!("ccm-update-test-{}-{}", std::process::id(), SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos()));
        let target = root.join("app"); let stage = root.join("stage"); let backup = root.join("backup");
        fs::create_dir_all(target.join("engine")).unwrap(); fs::create_dir_all(stage.join("engine")).unwrap();
        fs::write(target.join(EXE), b"old").unwrap(); fs::write(stage.join(EXE), b"new").unwrap();
        fs::write(target.join("engine/old.dll"), b"old engine").unwrap(); fs::write(stage.join("engine/new.dll"), b"new engine").unwrap();
        fs::write(target.join("user-backup.zip"), b"user data").unwrap();
        assert!(replace_portable(&target, &stage, &backup, Some(2)).is_err());
        assert_eq!(fs::read(target.join(EXE)).unwrap(), b"old"); assert!(target.join("engine/old.dll").is_file());
        assert!(!target.join("engine/new.dll").exists()); assert_eq!(fs::read(target.join("user-backup.zip")).unwrap(), b"user data");
        fs::remove_dir(&backup).unwrap(); replace_portable(&target, &stage, &backup, None).unwrap();
        assert_eq!(fs::read(target.join(EXE)).unwrap(), b"new"); assert!(target.join("engine/new.dll").is_file());
        assert_eq!(fs::read(backup.join(EXE)).unwrap(), b"old"); assert!(target.join("user-backup.zip").is_file());
        fs::remove_dir_all(root).unwrap();
    }
}
