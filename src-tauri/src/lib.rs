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
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::time::{Duration, Instant};

#[cfg(windows)]
use std::os::windows::process::CommandExt;
#[cfg(windows)]
use std::os::windows::io::AsRawHandle;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use rand::RngCore;
#[cfg(partner_pet)]
use serde::Serialize;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
#[cfg(partner_pet)]
use tauri::webview::PageLoadEvent;
use tauri::{
    AppHandle, Emitter, Manager, RunEvent, State, WebviewUrl, WebviewWindowBuilder, WindowEvent,
};
mod update;
#[cfg(partner_pet)]
mod pet;
#[cfg(partner_pet)]
use pet::*;
mod tray;
use tray::*;
mod sidecar;
use sidecar::*;
#[cfg(test)]
use update::installer_command;
#[cfg(partner_pet)]
use tauri::{EventTarget, LogicalSize, PhysicalPosition, PhysicalSize};
use tauri_plugin_autostart::MacosLauncher;

const TRAY_ID: &str = "main-tray";
const MAIN_LABEL: &str = "main";

/// The pet webview is a second page in the same process; without this gate it could invoke the shell's own
/// commands — start an installer, hide the tray, quit. Tauri's ACL is the general answer, but declaring an app
/// manifest makes every app command deny-by-default for *every* window, including `main`, and it would have to
/// stay correct while `PARTNER_PET=0` compiles the pet commands out. A label check at the top of each command is
/// the whole rule in one place.
fn require_caller(caller: &tauri::WebviewWindow, allowed: &[&str]) -> Result<(), String> {
    if allowed.contains(&caller.label()) {
        Ok(())
    } else {
        Err(format!(
            "command not available to window {}",
            caller.label()
        ))
    }
}






pub fn run() {
    let builder = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| show_main_window(app)))
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
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

            // The UI reads these globals; it never has a token of its own. A `PARTNER_PET=0` build compiles the pet
            // commands out, and the settings page reads `__PARTNER_PET__` to hide its pet controls.
            let script = format!(
                "window.__PARTNER_TOKEN__ = {token:?}; window.__PARTNER_PORT__ = {port}; \
                 window.__PARTNER_PET__ = {};",
                cfg!(partner_pet)
            );

            WebviewWindowBuilder::new(app, "main", WebviewUrl::default())
                .title("Partner")
                // No native title bar: the UI draws its own drag area and window buttons in the top bar.
                .decorations(false)
                .inner_size(1100.0, 720.0)
                .min_inner_size(800.0, 520.0)
                .initialization_script(&script)
                .build()?;

            app.manage(Sidecar::new(port, token.clone())?);

            #[cfg(partner_pet)]
            {
                app.manage(ShellSession {
                    port,
                    token: token.clone(),
                });
                // Read from the shell's own file, not from the API settings: the pet is up before the sidecar has
                // necessarily answered, exactly like the tray preference.
                if read_pet_pref() {
                    if let Err(error) = create_pet_window(app.handle(), true) {
                        append_api_log("shell", &format!("the pet window did not open: {error}"));
                    }
                }
            }

            supervise_sidecar(app.handle().clone(), port, token, 0);
            Ok(())
        })
        .on_window_event(|window, event| {
            #[cfg(partner_pet)]
            if window.label() == PET_LABEL {
                if matches!(event, WindowEvent::Moved(_) | WindowEvent::Resized(_)) {
                    remember_pet_geometry(window);
                }
                // A close from outside the setting and the tray (Alt+F4, the taskbar) means "the pet is gone":
                // hiding instead would leave `pet-enabled` promising a window nobody can see while its page keeps
                // rendering. Destroy it, clear the preference, and tell the settings page.
                if let WindowEvent::CloseRequested { api, .. } = event {
                    api.prevent_close();
                    let _ = window.destroy();
                    PET_WINDOW_SHOWN.store(false, Ordering::SeqCst);
                    if let Err(error) = write_pet_pref(false) {
                        append_api_log(
                            "shell",
                            &format!("the pet preference could not be cleared: {error}"),
                        );
                    }
                    let _ = window.app_handle().emit_to(MAIN_LABEL, PET_EVENT, false);
                }
                return;
            }

            if let WindowEvent::CloseRequested { api, .. } = event {
                if window.label() != "main" {
                    return;
                }
                let enabled = window
                    .app_handle()
                    .try_state::<TrayEnabled>()
                    .map(|state| state.0.load(Ordering::SeqCst))
                    .unwrap_or(false);
                if enabled {
                    api.prevent_close();
                    let _ = window.hide();
                    let _ = window.emit("partner:visibility", false);
                } else {
                    // Without a tray, closing `main` is how the app quits, and a live pet window would keep the
                    // event loop (and the process) alive. Drop it here so the exit is the same either way.
                    #[cfg(partner_pet)]
                    destroy_pet_window(window.app_handle());
                }
            }
        });

    #[cfg(partner_pet)]
    let builder = builder.invoke_handler(tauri::generate_handler![
        tray::get_tray_enabled,
        tray::set_tray_enabled,
        update::install_update,
        sidecar::sidecar_diagnostic,
        sidecar::retry_sidecar,
        pet::get_pet_enabled,
        pet::set_pet_enabled,
        pet::pet_set_passthrough,
        pet::pet_cursor,
        pet::pet_move_by,
        pet::pet_set_size,
        pet::pet_always_on_top,
        pet::pet_reset_position
    ]);
    #[cfg(not(partner_pet))]
    let builder = builder.invoke_handler(tauri::generate_handler![
        tray::get_tray_enabled,
        tray::set_tray_enabled,
        update::install_update,
        sidecar::sidecar_diagnostic,
        sidecar::retry_sidecar
    ]);

    builder
        .build(tauri::generate_context!())
        .expect("failed to build the shell")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                if let Some((_, Err(error))) = stop_sidecar(app) {
                    append_api_log("shell", &error);
                }
            }
        });
}


fn app_data_dir() -> Option<PathBuf> {
    Some(PathBuf::from(std::env::var_os("LOCALAPPDATA")?).join("Partner"))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// An update installs over the previous version; it never runs the installer's uninstall step.
    #[test]
    fn an_update_runs_the_installer_in_update_mode() {
        let command = installer_command(Path::new("C:/installs/partner-setup.exe"));
        let args: Vec<String> = command
            .get_args()
            .map(|argument| argument.to_string_lossy().into_owned())
            .collect();

        assert_eq!(args, ["/UPDATE", "/P"]);
    }

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
