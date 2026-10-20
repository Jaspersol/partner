//! The desktop shell: it hosts the Vue UI in a webview and owns the API sidecar's lifetime.
//!
//! Two things are load-bearing here. The shell picks a free port, generates a token, and passes both to the
//! `partner-api` sidecar — the token through the child's environment, never a command line and never a file —
//! then injects the same token into the webview. The window is built without waiting for the sidecar to finish
//! starting, and the UI retries until `/api/health` answers. The stdout pipe is drained on a thread for the
//! lifetime of the process: the API logs requests to stdout, and an unread pipe fills up and blocks the server.

use std::io::{BufRead, BufReader};
use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use rand::RngCore;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{
    AppHandle, Manager, RunEvent, State, WebviewUrl, WebviewWindowBuilder, WindowEvent,
};
use tauri_plugin_autostart::MacosLauncher;

const TRAY_ID: &str = "main-tray";

/// Keeps the sidecar handle so it can be killed when the shell exits.
struct Sidecar(Mutex<Option<Child>>);

/// Whether closing the window hides it to the tray instead of quitting. Mirrors the file on disk.
struct TrayEnabled(AtomicBool);

/// The preference lives beside the workspace rather than in the API's settings: it is the shell's, and the shell
/// needs it before (and without) the API answering.
fn tray_pref_path() -> Option<PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(PathBuf::from(base).join("Partner").join("tray-enabled"))
}

fn read_tray_pref() -> bool {
    tray_pref_path()
        .and_then(|path| std::fs::read_to_string(path).ok())
        .map(|text| text.trim() == "1")
        .unwrap_or(false)
}

fn write_tray_pref(enabled: bool) -> Result<(), String> {
    let path = tray_pref_path().ok_or("LOCALAPPDATA is not set")?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::write(path, if enabled { "1" } else { "0" }).map_err(|error| error.to_string())
}

fn show_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn create_tray(app: &AppHandle) -> tauri::Result<()> {
    if app.tray_by_id(TRAY_ID).is_some() {
        return Ok(());
    }
    let open = MenuItem::with_id(app, "open", "打开partner", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出partner", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&open, &quit])?;

    let mut builder = TrayIconBuilder::with_id(TRAY_ID)
        .tooltip("Partner")
        .menu(&menu)
        // Left click reopens the window; the menu is for the right button.
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" => show_main_window(app),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle());
            }
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder.build(app)?;
    Ok(())
}

#[tauri::command]
fn get_tray_enabled(state: State<'_, TrayEnabled>) -> bool {
    state.0.load(Ordering::SeqCst)
}

#[tauri::command]
fn set_tray_enabled(
    app: AppHandle,
    state: State<'_, TrayEnabled>,
    enabled: bool,
) -> Result<(), String> {
    if enabled {
        create_tray(&app).map_err(|error| error.to_string())?;
    } else {
        // Dropping the tray icon removes it from the notification area.
        let _ = app.remove_tray_by_id(TRAY_ID);
    }
    write_tray_pref(enabled)?;
    state.0.store(enabled, Ordering::SeqCst);
    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
        .invoke_handler(tauri::generate_handler![get_tray_enabled, set_tray_enabled])
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                let enabled = window
                    .app_handle()
                    .try_state::<TrayEnabled>()
                    .map(|state| state.0.load(Ordering::SeqCst))
                    .unwrap_or(false);
                if enabled && window.label() == "main" {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .setup(|app| {
            let tray_enabled = read_tray_pref();
            app.manage(TrayEnabled(AtomicBool::new(tray_enabled)));
            if tray_enabled {
                create_tray(app.handle())?;
            }
            let port = free_port()?;
            // The shell owns the token: generated here, passed to the sidecar through the environment (never
            // argv, never a file), and injected into the webview. The window does not wait for the API to finish
            // starting; it appears at once and the UI retries until /api/health answers.
            let token = generate_token();
            let child = start_api(port, &token)?;

            // The UI reads these two globals; it never has a token of its own.
            let script =
                format!("window.__PARTNER_TOKEN__ = {token:?}; window.__PARTNER_PORT__ = {port};");

            WebviewWindowBuilder::new(app, "main", WebviewUrl::default())
                .title("Partner")
                // No native title bar: the UI draws its own drag area and window buttons in the top bar.
                .decorations(false)
                .inner_size(1100.0, 720.0)
                .min_inner_size(800.0, 520.0)
                .initialization_script(&script)
                .build()?;

            app.manage(Sidecar(Mutex::new(Some(child))));
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("failed to build the shell")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                if let Some(state) = app.try_state::<Sidecar>() {
                    if let Ok(mut guard) = state.0.lock() {
                        if let Some(mut child) = guard.take() {
                            let _ = child.kill();
                            let _ = child.wait();
                        }
                    }
                }
            }
        });
}

/// Asks the OS for an unused port. The listener is dropped immediately; the API binds it moments later.
fn free_port() -> Result<u16, String> {
    let listener = TcpListener::bind("127.0.0.1:0").map_err(|error| error.to_string())?;
    let port = listener
        .local_addr()
        .map_err(|error| error.to_string())?
        .port();
    drop(listener);
    Ok(port)
}

fn start_api(port: u16, token: &str) -> Result<Child, String> {
    let exe = api_exe_path()?;
    let workspace = workspace_dir()?;

    let mut child = Command::new(&exe)
        .arg(&workspace)
        .arg(port.to_string())
        // The token travels in the child's environment, never argv and never a file. The child also watches
        // this pipe: if the shell dies without running its cleanup, the pipe closes and the API exits instead
        // of lingering as an orphan holding the port and the SQLite lock. The handle must stay alive for the
        // whole process, which is why it is never taken out of the Child.
        .env("PARTNER_API_PARENT_WATCH", "1")
        // The local engines are a separate module that lives beside the app (<app>/engines) and is downloaded
        // from the settings page instead of shipped in the installer. The API cannot reliably infer where the
        // app was installed from its own location, so the shell, which knows, says so.
        .env("PARTNER_APP_DIR", app_dir()?)
        .env("PARTNER_API_TOKEN", token)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| format!("could not start {}: {error}", exe.display()))?;

    let stdout = child.stdout.take().ok_or("the sidecar has no stdout")?;
    let stderr = child.stderr.take().ok_or("the sidecar has no stderr")?;

    // Drain both pipes for the lifetime of the process: the API logs to stdout, and an unread pipe fills up
    // and blocks the server. Nothing is read back for startup — the token is already ours, and the UI retries
    // /api/health instead of waiting on a line here.
    std::thread::spawn(
        move || {
            for _ in BufReader::new(stdout).lines().map_while(Result::ok) {}
        },
    );
    std::thread::spawn(
        move || {
            for _ in BufReader::new(stderr).lines().map_while(Result::ok) {}
        },
    );

    Ok(child)
}

/// A fresh bearer token for this launch. Any strong random string works — the API compares the bytes it is
/// given, so the only requirement is that it survives the round-trip through the environment and the
/// initialization script. Hex over URL-safe base64 only because it needs no extra dependency.
fn generate_token() -> String {
    let mut bytes = [0u8; 32];
    rand::thread_rng().fill_bytes(&mut bytes);
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Where the sidecar lives. A release shell only ever uses the folder bundled beside it; a debug shell
/// prefers the newest .NET build output, so editing the API and re-running the shell picks up the change
/// instead of silently using whatever the last `dotnet publish` produced.
fn api_exe_path() -> Result<PathBuf, String> {
    let bundled = std::env::current_exe().ok().and_then(|current| {
        current
            .parent()
            .map(|directory| directory.join("api").join("partner-api.exe"))
    });

    let debug_root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("src")
        .join("Partner.Api")
        .join("bin")
        .join("Debug");

    let build_output = newest_api_build(&debug_root);

    if cfg!(debug_assertions) {
        if let Some(path) = build_output {
            return Ok(path);
        }
    }

    if let Some(path) = bundled.filter(|path| path.exists()) {
        return Ok(path);
    }

    build_output.ok_or_else(|| {
        format!(
            "partner-api.exe not found under <shell>/api or under {} — run \"dotnet build\" first",
            debug_root.display()
        )
    })
}

/// The most recently written `partner-api.exe` under `bin/Debug/<framework>/`.
///
/// The framework folder is scanned for rather than spelled out: the moniker carries the Windows SDK version
/// (`net8.0-windows10.0.19041.0`), so a path pinned to `net8.0` never exists — and a debug shell then falls
/// through to the bundled snapshot, which is exactly the staleness this function exists to prevent. Several
/// framework folders can also coexist after a target framework change, so newest wins.
fn newest_api_build(debug_root: &std::path::Path) -> Option<PathBuf> {
    std::fs::read_dir(debug_root)
        .ok()?
        .filter_map(Result::ok)
        .map(|entry| entry.path().join("partner-api.exe"))
        .filter(|candidate| candidate.is_file())
        .filter_map(|candidate| {
            let modified = std::fs::metadata(&candidate).ok()?.modified().ok()?;
            Some((modified, candidate))
        })
        .max_by_key(|(modified, _)| *modified)
        .map(|(_, candidate)| candidate)
}

/// The directory the app is installed in. A debug shell reports the repository root, where `engines/` lives
/// during development; the `..` is left for the API to normalize, because canonicalizing on Windows would
/// produce a `\\?\` path.
fn app_dir() -> Result<PathBuf, String> {
    if cfg!(debug_assertions) {
        return Ok(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".."));
    }

    std::env::current_exe()
        .ok()
        .and_then(|current| current.parent().map(PathBuf::from))
        .ok_or_else(|| "cannot locate the application directory".to_string())
}

/// One workspace per user, shared with any hand-started API: the session log is the source of truth either way.
fn workspace_dir() -> Result<PathBuf, String> {
    let base = std::env::var("LOCALAPPDATA").map_err(|_| "LOCALAPPDATA is not set".to_string())?;
    Ok(PathBuf::from(base).join("Partner").join("workspace"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch(name: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!("partner-{}-{}", name, std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        root
    }

    /// The scan exists because the framework folder is not a constant: the moniker carries the Windows SDK
    /// version, so a path pinned to `net8.0` never exists — and a debug shell then falls through to the
    /// bundled snapshot, which is the staleness the function was written to prevent. Several folders can also
    /// coexist after a target framework change, so the newest has to win.
    #[test]
    fn the_newest_framework_folder_wins() {
        let root = scratch("sidecar-newest");

        let older = root.join("net8.0-windows");
        let newer = root.join("net8.0-windows10.0.19041.0");
        std::fs::create_dir_all(&older).unwrap();
        std::fs::create_dir_all(&newer).unwrap();

        std::fs::write(older.join("partner-api.exe"), b"older").unwrap();
        std::thread::sleep(std::time::Duration::from_millis(20));
        std::fs::write(newer.join("partner-api.exe"), b"newer").unwrap();

        assert_eq!(
            newest_api_build(&root),
            Some(newer.join("partner-api.exe")),
            "the most recently built sidecar should be chosen"
        );

        let _ = std::fs::remove_dir_all(&root);
    }

    /// A framework folder without the binary (a partially built target) must not be picked, and a directory
    /// with nothing at all must report "nothing found" rather than a path that does not exist.
    #[test]
    fn a_folder_without_the_sidecar_is_not_a_candidate() {
        let root = scratch("sidecar-empty");
        std::fs::create_dir_all(root.join("net8.0-windows10.0.19041.0")).unwrap();

        assert_eq!(newest_api_build(&root), None);

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn a_missing_build_root_is_not_an_error() {
        assert_eq!(
            newest_api_build(&PathBuf::from("no-such-directory-here")),
            None
        );
    }
}
