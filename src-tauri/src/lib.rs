//! The desktop shell: it hosts the Vue UI in a webview and owns the API sidecar's lifetime.
//!
//! Two things are load-bearing here. The shell picks a free port, generates a token, and passes both to the
//! `partner-api` sidecar — the token through the child's environment, never a command line and never a file —
//! then injects the same token into the webview. The window is built without waiting for the sidecar to finish
//! starting, and the UI retries until `/api/health` answers. Both pipes feed a capped log: an unread pipe
//! fills up and blocks the server.

use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Child, Command, ExitStatus, Stdio};
use std::time::{Duration, Instant};

#[cfg(windows)]
use std::os::windows::process::CommandExt;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use rand::RngCore;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{
    AppHandle, Emitter, Manager, RunEvent, State, WebviewUrl, WebviewWindowBuilder, WindowEvent,
};
use tauri_plugin_autostart::MacosLauncher;

const TRAY_ID: &str = "main-tray";

/// Keeps the sidecar handle and marks intentional stops so the supervisor does not restart it.
struct Sidecar {
    child: Mutex<Option<Child>>,
    stopping: AtomicBool,
}

const MAX_LOG_BYTES: u64 = 10 * 1024 * 1024;
static LOG_GATE: Mutex<()> = Mutex::new(());

fn api_log_path() -> Option<PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(PathBuf::from(base).join("Partner").join("partner-api.log"))
}

fn append_api_log(stream: &str, line: &str) {
    let Some(path) = api_log_path() else {
        return;
    };

    let _guard = match LOG_GATE.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    };
    append_log_file(&path, MAX_LOG_BYTES, stream, line);
}

/// Rotates to `partner-api.1.log` at `cap`, keeping one previous file.
fn append_log_file(path: &std::path::Path, cap: u64, stream: &str, line: &str) {
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    if let Ok(metadata) = std::fs::metadata(path) {
        if metadata.len() >= cap {
            let rotated = path.with_extension("1.log");
            let _ = std::fs::remove_file(&rotated);
            let _ = std::fs::rename(path, &rotated);
        }
    }

    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "[{stream}] {line}");
    }
}

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
        let _ = window.emit("partner:visibility", true);
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

/// Starts the verified installer and quits, because the installer replaces the files this process runs from.
///
/// The path comes from the API's download state, but it is re-checked here: this is the step that executes a
/// downloaded file, so a path that is not an existing `.exe` must never reach `Command`.
#[tauri::command]
fn install_update(app: AppHandle, path: String) -> Result<(), String> {
    let installer = PathBuf::from(&path);
    let is_exe = installer
        .extension()
        .is_some_and(|extension| extension.eq_ignore_ascii_case("exe"));
    if !installer.is_file() || !is_exe {
        return Err(format!("not an installer: {path}"));
    }

    Command::new(&installer)
        .spawn()
        .map_err(|error| format!("could not start the installer: {error}"))?;

    // The installer replaces this executable, so the shell has to go. Let the installer start first: its own
    // check for a running instance then sees a process already on its way out instead of prompting the person
    // to close the app that just told them it was updating.
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(1200));
        app.exit(0);
    });

    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
        .invoke_handler(tauri::generate_handler![
            get_tray_enabled,
            set_tray_enabled,
            install_update
        ])
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
                    let _ = window.emit("partner:visibility", false);
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

            app.manage(Sidecar {
                child: Mutex::new(Some(child)),
                stopping: AtomicBool::new(false),
            });
            supervise_sidecar(app.handle().clone(), port, token);
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("failed to build the shell")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                if let Some(state) = app.try_state::<Sidecar>() {
                    // Mark the stop before killing the child so the supervisor does not restart it.
                    state.stopping.store(true, Ordering::SeqCst);
                    if let Ok(mut guard) = state.child.lock() {
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

/// `CREATE_NO_WINDOW`. Only this flag is set; the child still gets the inherited environment and the pipes.
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

fn start_api(port: u16, token: &str) -> Result<Child, String> {
    let exe = api_exe_path()?;
    let workspace = workspace_dir()?;

    let mut command = Command::new(&exe);
    command
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
        .stderr(Stdio::piped());

    // The release shell is a GUI process, so Windows would give the console-subsystem child its own console
    // window. Everything the API prints already goes to the pipes above and nothing reads its console, so the
    // flag suppresses that window without taking anything away from the child.
    #[cfg(windows)]
    command.creation_flags(CREATE_NO_WINDOW);

    let mut child = command
        .spawn()
        .map_err(|error| format!("could not start {}: {error}", exe.display()))?;

    let stdout = child.stdout.take().ok_or("the sidecar has no stdout")?;
    let stderr = child.stderr.take().ok_or("the sidecar has no stderr")?;

    // Drain both pipes; unread output would block the API, and the log keeps startup failures.
    pump_api_output(stdout, "stdout");
    pump_api_output(stderr, "stderr");

    Ok(child)
}

fn pump_api_output<R: std::io::Read + Send + 'static>(stream: R, name: &'static str) {
    std::thread::spawn(move || {
        for line in BufReader::new(stream).lines().map_while(Result::ok) {
            append_api_log(name, &line);
        }
    });
}

/// Restarts the sidecar after abnormal exits; the port and token stay fixed for the loaded webview.
fn supervise_sidecar(app: AppHandle, port: u16, token: String) {
    std::thread::spawn(move || {
        let mut delay = Duration::from_millis(500);
        let mut started = Instant::now();

        loop {
            std::thread::sleep(Duration::from_millis(500));

            let Some(state) = app.try_state::<Sidecar>() else {
                return;
            };
            if state.stopping.load(Ordering::SeqCst) {
                return;
            }

            let exited: Option<Option<ExitStatus>> = {
                let mut guard = match state.child.lock() {
                    Ok(guard) => guard,
                    Err(poisoned) => poisoned.into_inner(),
                };
                match guard.as_mut() {
                    Some(child) => match child.try_wait() {
                        Ok(None) => None,
                        Ok(Some(status)) => {
                            guard.take();
                            Some(Some(status))
                        }
                        Err(error) => {
                            append_api_log("shell", &format!("sidecar try_wait failed: {error}"));
                            None
                        }
                    },
                    None => Some(None),
                }
            };

            let Some(status) = exited else {
                continue;
            };

            if state.stopping.load(Ordering::SeqCst) {
                return;
            }

            // Reset after a long run; startup failures keep backing off up to 30 s.
            if started.elapsed() > Duration::from_secs(60) {
                delay = Duration::from_millis(500);
            }

            match status {
                Some(status) => append_api_log(
                    "shell",
                    &format!(
                        "sidecar exited (code {:?}); restarting in {} ms",
                        status.code(),
                        delay.as_millis()
                    ),
                ),
                None => append_api_log(
                    "shell",
                    &format!(
                        "sidecar is not running; restarting in {} ms",
                        delay.as_millis()
                    ),
                ),
            }

            std::thread::sleep(delay);
            if state.stopping.load(Ordering::SeqCst) {
                return;
            }

            match start_api(port, &token) {
                Ok(child) => {
                    let mut guard = match state.child.lock() {
                        Ok(guard) => guard,
                        Err(poisoned) => poisoned.into_inner(),
                    };
                    *guard = Some(child);
                    started = Instant::now();
                    delay = (delay * 2).min(Duration::from_secs(30));
                    append_api_log("shell", "sidecar restarted");
                }
                Err(error) => {
                    append_api_log("shell", &format!("sidecar restart failed: {error}"));
                    delay = (delay * 2).min(Duration::from_secs(30));
                }
            }
        }
    });
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
/// (`net10.0-windows10.0.19041.0`), so a path pinned to `net10.0` never exists — and a debug shell then falls
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
    /// version, so a path pinned to `net10.0` never exists — and a debug shell then falls through to the
    /// bundled snapshot, which is the staleness the function was written to prevent. Several folders can also
    /// coexist after a target framework change, so the newest has to win.
    #[test]
    fn the_newest_framework_folder_wins() {
        let root = scratch("sidecar-newest");

        let older = root.join("net10.0-windows");
        let newer = root.join("net10.0-windows10.0.19041.0");
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
        std::fs::create_dir_all(root.join("net10.0-windows10.0.19041.0")).unwrap();

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

    #[test]
    fn the_api_log_rotates_at_its_cap() {
        let root = scratch("sidecar-log");
        let path = root.join("partner-api.log");

        for i in 0..20 {
            append_log_file(
                &path,
                200,
                "stdout",
                &format!("line {i} padded to force rotation"),
            );
        }

        let current = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
        let rotated = std::fs::metadata(path.with_extension("1.log"))
            .map(|m| m.len())
            .unwrap_or(0);
        assert!(current > 0, "the log should still be written");
        assert!(rotated > 0, "rotation should have produced a previous file");
        assert!(
            current <= 1_000 && rotated <= 1_000,
            "both files should stay near the cap, got current={current} rotated={rotated}"
        );

        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("[stdout] line"), "the prefix should be kept");

        let _ = std::fs::remove_dir_all(&root);
    }
}
