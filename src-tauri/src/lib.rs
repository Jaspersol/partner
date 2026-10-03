//! The desktop shell: it hosts the Vue UI in a webview and owns the API sidecar's lifetime.
//!
//! Two things are load-bearing here. The shell picks a free port and starts `partner-api` on it, then reads
//! the token the API generated from its stdout and injects it into the webview — so the token never appears
//! in a command line, never lands in a file, and never has to be typed by a human. The stdout pipe is drained
//! on a thread for the lifetime of the process: the API logs requests to stdout, and an unread pipe fills up
//! and blocks the server.

use std::io::{BufRead, BufReader};
use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;

use tauri::{Manager, RunEvent, WebviewUrl, WebviewWindowBuilder};

/// Keeps the sidecar handle so it can be killed when the shell exits.
struct Sidecar(Mutex<Option<Child>>);

pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            let port = free_port()?;
            let (child, token) = start_api(port)?;

            // The UI reads these two globals; it never has a token of its own.
            let script = format!("window.__PARTNER_TOKEN__ = {token:?}; window.__PARTNER_PORT__ = {port};");

            WebviewWindowBuilder::new(app, "main", WebviewUrl::default())
                .title("Partner")
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
    let port = listener.local_addr().map_err(|error| error.to_string())?.port();
    drop(listener);
    Ok(port)
}

fn start_api(port: u16) -> Result<(Child, String), String> {
    let exe = api_exe_path()?;
    let workspace = workspace_dir()?;

    let mut child = Command::new(&exe)
        .arg(&workspace)
        .arg(port.to_string())
        // The child watches this pipe: if the shell dies without running its cleanup, the pipe closes and the
        // API exits instead of lingering as an orphan holding the port and the SQLite lock. The handle must
        // stay alive for the whole process, which is why it is never taken out of the Child.
        .env("PARTNER_API_PARENT_WATCH", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| format!("could not start {}: {error}", exe.display()))?;

    let stdout = child.stdout.take().ok_or("the sidecar has no stdout")?;
    let stderr = child.stderr.take().ok_or("the sidecar has no stderr")?;

    // Read until the token appears, then keep draining so the API never blocks writing its log.
    let (token_sender, token_receiver) = std::sync::mpsc::channel::<String>();
    std::thread::spawn(move || {
        let mut sender = Some(token_sender);
        for line in BufReader::new(stdout).lines().map_while(Result::ok) {
            if let Some(rest) = line.strip_prefix("token: ") {
                if let Some(channel) = sender.take() {
                    let _ = channel.send(rest.trim().to_string());
                }
            }
        }
    });

    // The API's own log is the only diagnostic if it fails to start, so keep the last of it.
    let (error_sender, error_receiver) = std::sync::mpsc::channel::<String>();
    std::thread::spawn(move || {
        let mut sender = Some(error_sender);
        for line in BufReader::new(stderr).lines().map_while(Result::ok) {
            if let Some(channel) = sender.take() {
                let _ = channel.send(line);
            }
        }
    });

    let token = token_receiver
        .recv()
        .map_err(|_| match error_receiver.try_recv() {
            Ok(detail) => format!("the API exited before reporting a token: {detail}"),
            Err(_) => "the API exited before reporting a token".to_string(),
        })?;

    Ok((child, token))
}

/// Where the sidecar lives. A release shell only ever uses the folder bundled beside it; a debug shell
/// prefers the newest .NET build output, so editing the API and re-running the shell picks up the change
/// instead of silently using whatever the last `dotnet publish` produced.
fn api_exe_path() -> Result<PathBuf, String> {
    let bundled = std::env::current_exe()
        .ok()
        .and_then(|current| current.parent().map(|directory| directory.join("api").join("partner-api.exe")));

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
        assert_eq!(newest_api_build(&PathBuf::from("no-such-directory-here")), None);
    }
}
