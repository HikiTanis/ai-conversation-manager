#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::{io::{Read, Write}, path::PathBuf, process::{Command, Stdio}, time::{Duration, Instant}};
use tauri::Manager;
use std::sync::atomic::{AtomicBool, Ordering};
static OPERATING: AtomicBool = AtomicBool::new(false);
mod updater;
struct OperationGuard;
impl Drop for OperationGuard { fn drop(&mut self) { OPERATING.store(false, Ordering::SeqCst); } }

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
enum Operation { Backup, PackageInfo, Inspect, Import, Trash, Restore, Purge, TrashProject, PlanDelete, Delete, Archive, Unarchive, Recover, PlanRepair, Repair }
#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OperationRequest {
    action: Operation, provider: Provider, home: Option<String>, language: String,
    id: Option<String>, ids: Option<Vec<String>>, project_ids: Option<Vec<String>>,
    output: Option<String>, package: Option<String>, target: Option<String>,
    mode: Option<String>, conflict: Option<String>, token: Option<String>, project_mode: Option<String>,
    #[serde(default)] include_files: bool,
    #[serde(default)] map: bool,
    #[serde(default)] restore_files: bool,
    #[serde(default)] permanent: bool,
    #[serde(default)] batch: bool,
    project_confirmation: Option<String>,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
enum Provider { Codex, Dsh }
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
enum Action { List, Preview, Storage }
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ReadRequest {
    action: Action,
    provider: Provider,
    home: Option<String>,
    language: String,
    project_id: Option<String>,
    id: Option<String>,
    offset: Option<u32>,
    session_path: Option<String>,
}
impl ReadRequest {
    fn validate(&self) -> Result<(), String> {
        if self.language != "zh" && self.language != "en" { return Err("Unsupported language".into()); }
        if let Some(home) = &self.home {
            if home.len() > 4096 || (!home.is_empty() && !PathBuf::from(home).is_absolute()) { return Err("Data folder must be an absolute path".into()); }
        }
        if matches!(self.action, Action::Preview) && (self.id.as_deref().unwrap_or("").is_empty() || self.project_id.as_deref().unwrap_or("").is_empty()) { return Err("Select a conversation first".into()); }
        if matches!(self.action, Action::Storage) && self.project_id.as_deref().unwrap_or("").is_empty() { return Err("Select a project first".into()); }
        Ok(())
    }
}

fn engine_path(app: &tauri::AppHandle) -> Result<PathBuf, String> {
    let packaged = app.path().resource_dir().map_err(|e| e.to_string())?.join("engine/CodexConversationManager.exe");
    if packaged.is_file() { return Ok(packaged); }
    #[cfg(debug_assertions)] {
        let development = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("engine/CodexConversationManager.exe");
        if development.is_file() { return Ok(development); }
    }
    Err("Missing compatibility engine. Run npm run prepare:engine, then rebuild the desktop app.".into())
}

fn execute(engine: PathBuf, request: ReadRequest) -> Result<serde_json::Value, String> {
    request.validate()?;
    let payload = serde_json::to_vec(&request).map_err(|e| e.to_string())?;
    if payload.len() > 16384 { return Err("Request too large".into()); }
    execute_payload(engine, payload, false)
}
fn execute_payload(engine: PathBuf, payload: Vec<u8>, operation: bool) -> Result<serde_json::Value, String> {
    let mut command = Command::new(&engine);
    command.arg(if operation { "--desktop-operation" } else { "--desktop-read" }).current_dir(engine.parent().ok_or("Invalid engine location")?)
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null());
    #[cfg(windows)] {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000); // CREATE_NO_WINDOW
    }
    let mut child = command.spawn().map_err(|e| format!("Cannot start compatibility engine: {e}"))?;
    let mut stdin = child.stdin.take().ok_or("Missing engine input")?;
    if let Err(e) = stdin.write_all(&payload) { let _ = child.kill(); let _ = child.wait(); return Err(e.to_string()); }
    drop(stdin);
    let stdout = child.stdout.take().ok_or("Missing engine output")?;
    let reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.take(32 * 1024 * 1024 + 1).read_to_end(&mut bytes).map(|_| bytes)
    });
    let start = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) if operation || start.elapsed() < Duration::from_secs(90) => std::thread::sleep(Duration::from_millis(30)),
            status => {
                let _ = child.kill(); let _ = child.wait(); let _ = reader.join();
                return Err(match status { Err(e) => e.to_string(), _ => "Reading timed out. Narrow the data folder and retry.".into() });
            }
        }
    }
    let bytes = reader.join().map_err(|_| "Engine output reader failed")?.map_err(|e| e.to_string())?;
    if bytes.len() > 32 * 1024 * 1024 { return Err("Catalog is too large for this preview".into()); }
    let response: serde_json::Value = serde_json::from_slice(&bytes).map_err(|e| format!("Invalid engine response: {e}"))?;
    if response["ok"] != true { return Err(response["error"].as_str().unwrap_or("Engine read failed").to_owned()); }
    Ok(response["result"].clone())
}
#[tauri::command]
async fn run_operation(app: tauri::AppHandle, request: OperationRequest) -> Result<serde_json::Value, String> {
    ReadRequest { action: Action::List, provider: request.provider.clone(), home: request.home.clone(), language: request.language.clone(), project_id: None, id: None, session_path: None, offset: None }.validate()?;
    let payload = serde_json::to_vec(&request).map_err(|e| e.to_string())?;
    if payload.len() > 262144 { return Err("Request too large".into()); }
    let engine = engine_path(&app)?;
    if OPERATING.swap(true, Ordering::SeqCst) { return Err("Another operation is running".into()); }
    let guard = OperationGuard;
    tauri::async_runtime::spawn_blocking(move || { let _guard = guard; execute_payload(engine, payload, true) }).await.map_err(|e| e.to_string())?
}

#[tauri::command]
async fn read_catalog(app: tauri::AppHandle, request: ReadRequest) -> Result<serde_json::Value, String> {
    request.validate()?;
    let engine = engine_path(&app)?;
    tauri::async_runtime::spawn_blocking(move || execute(engine, request)).await.map_err(|e| e.to_string())?
}
fn main() {
    if updater::run_helper() { return; }
    tauri::Builder::default().plugin(tauri_plugin_dialog::init())
        .on_window_event(|_, event| { if let tauri::WindowEvent::CloseRequested { api, .. } = event { if OPERATING.load(Ordering::SeqCst) { api.prevent_close(); } } })
        .invoke_handler(tauri::generate_handler![read_catalog, run_operation, updater::check_update, updater::install_update])
        .run(tauri::generate_context!()).expect("Unable to start AI Conversation Manager");
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn write_actions_and_arbitrary_arguments_are_rejected() {
        for payload in [r#"{"action":"delete","provider":"codex","language":"zh"}"#, r#"{"action":"list","provider":"other","language":"zh"}"#, r#"{"action":"list","provider":"codex","language":"zh","executable":"other.exe"}"#] {
            assert!(serde_json::from_str::<ReadRequest>(payload).is_err());
        }
    }
    #[test]
    fn preview_requires_identity_and_absolute_home() {
        let mut request: ReadRequest = serde_json::from_str(r#"{"action":"preview","provider":"dsh","language":"zh"}"#).unwrap();
        assert!(request.validate().is_err());
        request.id = Some("test".into()); request.project_id = Some("project".into());
        assert!(request.validate().is_ok());
        request.home = Some("../private".into()); assert!(request.validate().is_err());
    }
}
