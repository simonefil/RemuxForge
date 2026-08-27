use serde::Deserialize;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;
use tauri::{Manager, RunEvent};
use tauri_plugin_shell::ShellExt;
use tauri_plugin_shell::process::{CommandChild, CommandEvent};

const READY_PREFIX: &str = "REMUXFORGE_READY ";

struct SidecarState {
    child: Mutex<Option<CommandChild>>,
    exiting: AtomicBool,
    generation: AtomicU64,
    loader_url: Mutex<Option<tauri::Url>>,
}

#[derive(Deserialize)]
struct ReadyMessage {
    url: String,
}

fn navigate_to_loader(app: &tauri::AppHandle, error: Option<&str>) {
    let state = app.state::<SidecarState>();
    let loader_url = state
        .loader_url
        .lock()
        .ok()
        .and_then(|url| url.as_ref().cloned());

    if let (Some(window), Some(mut url)) = (app.get_webview_window("main"), loader_url) {
        url.set_query(None);
        if let Some(message) = error {
            url.query_pairs_mut().append_pair("error", message);
        }
        let _ = window.navigate(url);
    }
}

fn handle_ready_line(app: &tauri::AppHandle, line: &str) {
    let Some(payload) = line.strip_prefix(READY_PREFIX) else {
        return;
    };

    match serde_json::from_str::<ReadyMessage>(payload.trim()) {
        Ok(ready) => match ready.url.parse::<tauri::Url>() {
            Ok(url) if url.scheme() == "http" && url.host_str() == Some("127.0.0.1") => {
                if let Some(window) = app.get_webview_window("main") {
                    if let Err(error) = window.navigate(url) {
                        navigate_to_loader(app, Some(&error.to_string()));
                    }
                }
            }
            Ok(_) => {
                navigate_to_loader(app, Some("The RemuxForge server returned a non-local URL"))
            }
            Err(error) => navigate_to_loader(app, Some(&error.to_string())),
        },
        Err(error) => navigate_to_loader(app, Some(&error.to_string())),
    }
}

fn start_sidecar(app: &tauri::AppHandle) -> Result<(), String> {
    let state = app.state::<SidecarState>();
    state.exiting.store(false, Ordering::SeqCst);
    let generation = state.generation.fetch_add(1, Ordering::SeqCst) + 1;
    let data_dir = app
        .path()
        .app_local_data_dir()
        .map_err(|error| error.to_string())?;
    std::fs::create_dir_all(&data_dir).map_err(|error| error.to_string())?;
    #[cfg(target_os = "windows")]
    let web_root = std::env::current_exe()
        .map_err(|error| error.to_string())?
        .parent()
        .ok_or_else(|| "The RemuxForge executable directory was not found".to_string())?
        .join("wwwroot");
    #[cfg(not(target_os = "windows"))]
    let web_root = app
        .path()
        .resource_dir()
        .map_err(|error| error.to_string())?
        .join("wwwroot");
    if !web_root.is_dir() {
        return Err(format!(
            "RemuxForge web resources were not found at {}",
            web_root.display()
        ));
    }

    let command = app
        .shell()
        .sidecar("remuxforge-web")
        .map_err(|error| error.to_string())?
        .args(["--desktop", "--port", "0"])
        .env("REMUXFORGE_DATA_DIR", data_dir.as_os_str())
        .env("REMUXFORGE_WEB_ROOT", web_root.as_os_str());

    let (mut receiver, child) = command.spawn().map_err(|error| error.to_string())?;
    state
        .child
        .lock()
        .map_err(|error| error.to_string())?
        .replace(child);

    let app_handle = app.clone();
    tauri::async_runtime::spawn(async move {
        let mut stdout_buffer = String::new();
        while let Some(event) = receiver.recv().await {
            match event {
                CommandEvent::Stdout(bytes) => {
                    stdout_buffer.push_str(&String::from_utf8_lossy(&bytes));
                    while let Some(newline_index) = stdout_buffer.find('\n') {
                        let line: String = stdout_buffer.drain(..=newline_index).collect();
                        handle_ready_line(&app_handle, line.trim());
                    }
                }
                CommandEvent::Stderr(bytes) => {
                    eprintln!("{}", String::from_utf8_lossy(&bytes));
                }
                CommandEvent::Error(error) => navigate_to_loader(&app_handle, Some(&error)),
                CommandEvent::Terminated(payload) => {
                    let state = app_handle.state::<SidecarState>();
                    if state.generation.load(Ordering::SeqCst) == generation {
                        if let Ok(mut child) = state.child.lock() {
                            child.take();
                        }
                        if !state.exiting.load(Ordering::SeqCst)
                            && app_handle.get_webview_window("main").is_some()
                        {
                            navigate_to_loader(
                                &app_handle,
                                Some(&format!(
                                    "The RemuxForge server stopped: {:?}",
                                    payload.code
                                )),
                            );
                        }
                    }
                    break;
                }
                _ => {}
            }
        }
    });

    Ok(())
}

fn stop_sidecar(app: &tauri::AppHandle) {
    let state = app.state::<SidecarState>();
    state.exiting.store(true, Ordering::SeqCst);
    state.generation.fetch_add(1, Ordering::SeqCst);
    let child = state.child.lock().ok().and_then(|mut guard| guard.take());
    if let Some(mut child) = child {
        let _ = child.write(b"SHUTDOWN\n");
        std::thread::sleep(Duration::from_millis(750));
        let _ = child.kill();
    }
}

#[tauri::command]
fn restart_sidecar(app: tauri::AppHandle) -> Result<(), String> {
    stop_sidecar(&app);
    start_sidecar(&app)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.show();
                let _ = window.set_focus();
            }
        }))
        .plugin(tauri_plugin_shell::init())
        .manage(SidecarState {
            child: Mutex::new(None),
            exiting: AtomicBool::new(false),
            generation: AtomicU64::new(0),
            loader_url: Mutex::new(None),
        })
        .setup(|app| {
            if let Some(window) = app.get_webview_window("main") {
                let loader_url = window.url()?;
                app.state::<SidecarState>()
                    .loader_url
                    .lock()
                    .map_err(|error| error.to_string())?
                    .replace(loader_url);
            }
            if let Err(error) = start_sidecar(app.handle()) {
                navigate_to_loader(app.handle(), Some(&error));
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![restart_sidecar])
        .build(tauri::generate_context!())
        .expect("error while building RemuxForge desktop");

    app.run(|app_handle, event| {
        if matches!(event, RunEvent::Exit) {
            stop_sidecar(app_handle);
        }
    });
}
