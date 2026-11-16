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
    #[cfg(partner_pet)]
    let pet = MenuItem::with_id(app, "pet", "显示/隐藏桌宠", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出partner", true, None::<&str>)?;
    #[cfg(partner_pet)]
    let menu = Menu::with_items(app, &[&open, &pet, &quit])?;
    #[cfg(not(partner_pet))]
    let menu = Menu::with_items(app, &[&open, &quit])?;

    let mut builder = TrayIconBuilder::with_id(TRAY_ID)
        .tooltip("Partner")
        .menu(&menu)
        // Left click reopens the window; the menu is for the right button.
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" => show_main_window(app),
            #[cfg(partner_pet)]
            "pet" => {
                let enabled = !read_pet_pref();
                // The menu handler must not block the event loop (see `set_pet_enabled`), so the toggle runs as a
                // task and the main window hears the outcome when it lands.
                let handle = app.clone();
                tauri::async_runtime::spawn(async move {
                    match set_pet_preference(&handle, enabled).await {
                        Ok(()) => {
                            let _ = handle.emit_to(MAIN_LABEL, PET_EVENT, enabled);
                        }
                        Err(error) => append_api_log(
                            "shell",
                            &format!("the pet window could not be toggled: {error}"),
                        ),
                    }
                });
            }
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
fn get_tray_enabled(
    caller: tauri::WebviewWindow,
    state: State<'_, TrayEnabled>,
) -> Result<bool, String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    Ok(state.0.load(Ordering::SeqCst))
}

#[tauri::command]
fn set_tray_enabled(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    state: State<'_, TrayEnabled>,
    enabled: bool,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL])?;
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

// The desktop pet: a second, transparent webview that renders a Live2D model. Everything in this block is behind
// `cfg(partner_pet)` (see build.rs), which is on unless a build sets `PARTNER_PET=0`.

/// The pet window's label, and the key every lookup and event filter uses.
#[cfg(partner_pet)]
const PET_LABEL: &str = "pet";

/// Sent to the `main` window with the resulting boolean whenever the pet is shown or hidden, from whichever
/// switch did it, so the settings page never has to poll the shell.
///
/// The same name is sent to the pet window itself with its own visibility, because that is the one event the
/// page listens for (`ui/src/pet/shellBridge.ts` `onVisibility`); a second name would be silently ignored.
#[cfg(partner_pet)]
const PET_EVENT: &str = "partner:pet";

/// The token and port the webviews are initialized with. The pet window can be created long after startup
/// (from the setting or the tray), so these values have to outlive the `main` window's builder.
#[cfg(partner_pet)]
struct ShellSession {
    port: u16,
    token: String,
}

/// The preference lives beside `tray-enabled` and for the same reason: it is the shell's, and the pet has to
/// come back at startup whether or not the API (which owns the settings document) is answering yet.
#[cfg(partner_pet)]
fn pet_pref_path() -> Option<PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(PathBuf::from(base).join("Partner").join("pet-enabled"))
}

#[cfg(partner_pet)]
fn read_pet_pref() -> bool {
    pet_pref_path()
        .and_then(|path| std::fs::read_to_string(path).ok())
        .map(|text| text.trim() == "1")
        .unwrap_or(false)
}

#[cfg(partner_pet)]
fn write_pet_pref(enabled: bool) -> Result<(), String> {
    let path = pet_pref_path().ok_or("LOCALAPPDATA is not set")?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::write(path, if enabled { "1" } else { "0" }).map_err(|error| error.to_string())
}

/// A rectangle in physical pixels relative to the desktop origin — the pet window's outer frame, or a monitor.
#[cfg(partner_pet)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct PetRect {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

#[cfg(partner_pet)]
impl PetRect {
    /// The size the window opens at before the model reports its canvas size.
    const DEFAULT_WIDTH: u32 = 300;
    const DEFAULT_HEIGHT: u32 = 360;
    /// Keeps the window's edge off the very corner of the screen, where a taskbar or a rounded corner hides it.
    const SCREEN_MARGIN: i32 = 24;
}

#[cfg(partner_pet)]
#[derive(Clone, Debug, PartialEq, Eq)]
struct MonitorRect {
    /// What the display reports as its name, when it reports one. Only used to restore onto the same display.
    name: Option<String>,
    x: i32,
    y: i32,
    width: u32,
    height: u32,
    /// The part not covered by the taskbar or a docked app bar. Parking uses this, not the full height: a pet
    /// dropped at the bottom-right corner of the screen would otherwise sit behind the taskbar and be unclickable.
    work_x: i32,
    work_y: i32,
    work_width: u32,
    work_height: u32,
}

#[cfg(partner_pet)]
impl MonitorRect {
    fn contains(&self, x: i32, y: i32) -> bool {
        let right = self.x.saturating_add(self.width as i32);
        let bottom = self.y.saturating_add(self.height as i32);
        x >= self.x && x < right && y >= self.y && y < bottom
    }

    /// Bottom-right of this display's work area, with the margin; clamped so a window taller than the work area still
    /// starts inside it instead of above the top edge.
    fn park(&self, rect: PetRect) -> PetRect {
        let x = self
            .work_x
            .saturating_add(self.work_width as i32)
            .saturating_sub(rect.width as i32)
            .saturating_sub(PetRect::SCREEN_MARGIN);
        let y = self
            .work_y
            .saturating_add(self.work_height as i32)
            .saturating_sub(rect.height as i32)
            .saturating_sub(PetRect::SCREEN_MARGIN);
        PetRect {
            x: x.max(self.work_x),
            y: y.max(self.work_y),
            ..rect
        }
    }
}

/// Keeps a restored pet window on a display that still exists.
///
/// A rect whose centre lands on any monitor is returned untouched: the user may deliberately have nudged it
/// half over an edge, and second-guessing that would move the window on every launch. Anything else — a
/// display that was unplugged, a resolution change, a file left by another machine — is parked on the primary
/// monitor's bottom-right, which is where a fresh window starts too.
///
/// `monitors` must have the primary monitor first. `available_monitors` promises no such order, so the caller
/// puts `primary_monitor` at the head: parking on a secondary display the user may unplug next time would only
/// repeat the problem.
#[cfg(partner_pet)]
fn clamp_pet_rect(rect: PetRect, monitors: &[MonitorRect]) -> PetRect {
    let Some(primary) = monitors.first() else {
        return rect;
    };
    let centre_x = rect.x.saturating_add((rect.width / 2) as i32);
    let centre_y = rect.y.saturating_add((rect.height / 2) as i32);
    if monitors
        .iter()
        .any(|monitor| monitor.contains(centre_x, centre_y))
    {
        return rect;
    }
    primary.park(rect)
}

/// Puts a saved window back where it was.
///
/// The saved monitor name is tried first, and only when the rect's centre is no longer on it. Two identical
/// displays swap places when one is replugged, so a saved rect lands on the other one; it is still on *a* monitor,
/// so [`clamp_pet_rect`] alone would leave it there. A monitor that no longer exists falls through to the clamp.
#[cfg(partner_pet)]
fn restore_pet_rect(geometry: PetGeometry, monitors: &[MonitorRect]) -> PetRect {
    let PetGeometry { rect, monitor } = geometry;
    let centre_x = rect.x.saturating_add((rect.width / 2) as i32);
    let centre_y = rect.y.saturating_add((rect.height / 2) as i32);

    if let Some(name) = monitor {
        if let Some(target) = monitors
            .iter()
            .find(|candidate| candidate.name.as_deref() == Some(name.as_str()))
        {
            if target.contains(centre_x, centre_y) {
                return rect;
            }
            // The same display, but the saved position is no longer on it: park at its corner, then run the
            // geometric clamp once more. `park` only pins the top-left inside the work area, so a monitor smaller
            // than the saved window leaves its right/bottom edges off-screen; the clamp re-validates the result
            // against every display (the frontend later shrinks the window with `setSize`).
            return clamp_pet_rect(target.park(rect), monitors);
        }
    }

    clamp_pet_rect(rect, monitors)
}

#[cfg(partner_pet)]
fn pet_rect_path() -> Option<PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(PathBuf::from(base).join("Partner").join("pet-window.json"))
}

/// The geometry file's contents: the window's rect and, when the display reported one, the name of the monitor it
/// sat on.
#[cfg(partner_pet)]
struct PetGeometry {
    rect: PetRect,
    monitor: Option<String>,
}

#[cfg(partner_pet)]
fn decode_pet_rect(text: &str) -> Option<PetRect> {
    Some(PetRect {
        x: i32::try_from(json_integer(text, "x")?).ok()?,
        y: i32::try_from(json_integer(text, "y")?).ok()?,
        width: u32::try_from(json_integer(text, "width")?).ok()?,
        height: u32::try_from(json_integer(text, "height")?).ok()?,
    })
}

#[cfg(partner_pet)]
fn read_pet_geometry() -> Option<PetGeometry> {
    let text = std::fs::read_to_string(pet_rect_path()?).ok()?;
    let rect = decode_pet_rect(&text)?;
    if rect.width == 0 || rect.height == 0 {
        return None;
    }
    Some(PetGeometry {
        rect,
        monitor: json_string(&text, "monitor"),
    })
}

#[cfg(partner_pet)]
fn write_pet_rect(geometry: PetGeometry) {
    let Some(path) = pet_rect_path() else {
        return;
    };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let PetGeometry { rect, monitor } = geometry;
    // The name is written as a JSON string only when the platform reported one; a display with no name keeps the
    // file's shape and the restore falls back to "is the rect on any monitor at all".
    let monitor = match monitor {
        Some(name) => format!(",\"monitor\":{}", json_quote(&name)),
        None => String::new(),
    };
    let text = format!(
        "{{\"x\":{},\"y\":{},\"width\":{},\"height\":{}{monitor}}}",
        rect.x, rect.y, rect.width, rect.height
    );
    let _ = std::fs::write(path, text);
}

/// Quotes a string as a JSON literal. Monitor names are plain ASCII in practice, but a name is outside this
/// module's control, so every quote and backslash is escaped rather than trusted.
#[cfg(partner_pet)]
fn json_quote(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    out.push('"');
    for character in value.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            control if (control as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", control as u32)),
            other => out.push(other),
        }
    }
    out.push('"');
    out
}

/// Reads one string member written by {@link json_quote}. Only the escapes that function emits are recognised,
/// which keeps this a decoder for this file rather than a JSON parser.
#[cfg(partner_pet)]
fn json_string(text: &str, key: &str) -> Option<String> {
    let quoted = format!("\"{key}\"");
    let after_key = &text[text.find(&quoted)? + quoted.len()..];
    let after_colon = after_key.trim_start().strip_prefix(':')?.trim_start();
    let mut characters = after_colon.strip_prefix('"')?.chars();
    let mut out = String::new();
    loop {
        match characters.next()? {
            '"' => return Some(out),
            '\\' => match characters.next()? {
                '"' => out.push('"'),
                '\\' => out.push('\\'),
                'n' => out.push('\n'),
                'r' => out.push('\r'),
                't' => out.push('\t'),
                'u' => {
                    let digits: String = characters.by_ref().take(4).collect();
                    out.push(char::from_u32(u32::from_str_radix(&digits, 16).ok()?)?);
                }
                // An escape this module never writes: treat the file as unreadable rather than guess.
                _ => return None,
            },
            other => out.push(other),
        }
    }
}

/// Reads one integer member of the flat object this module writes. Four numbers do not justify a JSON
/// dependency, and the file is only ever written by this function, so the shape is known.
#[cfg(partner_pet)]
fn json_integer(text: &str, key: &str) -> Option<i64> {
    let quoted = format!("\"{key}\"");
    let after_key = &text[text.find(&quoted)? + quoted.len()..];
    let after_colon = after_key.trim_start().strip_prefix(':')?.trim_start();
    let bytes = after_colon.as_bytes();
    let mut index = if bytes.first() == Some(&b'-') { 1 } else { 0 };
    let start = index;
    while index < bytes.len() && bytes[index].is_ascii_digit() {
        index += 1;
    }
    if index == start {
        return None;
    }
    let value: i64 = after_colon[start..index].parse().ok()?;
    Some(if start == 1 { -value } else { value })
}

/// Debounces the geometry file. A drag emits a `Moved` event per frame, and only the resting place matters, so
/// one writer keeps sleeping until no new rect arrives and then writes the newest.
#[cfg(partner_pet)]
struct PetRectSaver {
    pending: Mutex<Option<PetGeometry>>,
    running: AtomicBool,
}

#[cfg(partner_pet)]
static PET_RECT_SAVER: PetRectSaver = PetRectSaver {
    pending: Mutex::new(None),
    running: AtomicBool::new(false),
};

#[cfg(partner_pet)]
const PET_RECT_SAVE_DELAY: Duration = Duration::from_millis(500);

#[cfg(partner_pet)]
fn queue_pet_rect(geometry: PetGeometry) {
    let Ok(mut pending) = PET_RECT_SAVER.pending.lock() else {
        return;
    };
    *pending = Some(geometry);
    drop(pending);

    // The lock above is what makes dropping a rect impossible: `pending` is set before the flag is read here,
    // and the writer clears the flag under the same lock, so either this call spawns the next writer or the
    // running one still sees the rect.
    if PET_RECT_SAVER.running.swap(true, Ordering::SeqCst) {
        return;
    }

    std::thread::spawn(|| loop {
        std::thread::sleep(PET_RECT_SAVE_DELAY);

        let latest = PET_RECT_SAVER
            .pending
            .lock()
            .ok()
            .and_then(|mut pending| pending.take());

        if let Some(geometry) = latest {
            write_pet_rect(geometry);
            // Moving is still in progress: sleep again rather than writing per frame.
            continue;
        }

        let pending = match PET_RECT_SAVER.pending.lock() {
            Ok(pending) => pending,
            Err(poisoned) => poisoned.into_inner(),
        };
        if pending.is_some() {
            drop(pending);
            continue;
        }
        PET_RECT_SAVER.running.store(false, Ordering::SeqCst);
        return;
    });
}

#[cfg(partner_pet)]
fn desktop_monitors(app: &AppHandle) -> Vec<MonitorRect> {
    let of = |monitor: &tauri::Monitor| {
        let work = monitor.work_area();
        MonitorRect {
            name: monitor.name().cloned(),
            x: monitor.position().x,
            y: monitor.position().y,
            width: monitor.size().width,
            height: monitor.size().height,
            work_x: work.position.x,
            work_y: work.position.y,
            work_width: work.size.width,
            work_height: work.size.height,
        }
    };

    let mut monitors = Vec::new();
    if let Ok(Some(primary)) = app.primary_monitor() {
        monitors.push(of(&primary));
    }
    if let Ok(available) = app.available_monitors() {
        for monitor in &available {
            let rect = of(monitor);
            if !monitors.contains(&rect) {
                monitors.push(rect);
            }
        }
    }
    monitors
}

/// The initialization script shared with the `main` window: the pet talks to the API directly, so it needs the
/// same token and port globals.
#[cfg(partner_pet)]
fn pet_script(app: &AppHandle) -> Result<String, String> {
    let session = app
        .try_state::<ShellSession>()
        .ok_or("the shell session is not ready yet")?;
    Ok(format!(
        "window.__PARTNER_TOKEN__ = {:?}; window.__PARTNER_PORT__ = {};",
        session.token, session.port
    ))
}

/// Whether the pet window has been shown since it was created. The page-load hook runs on the UI thread, where a
/// window getter such as `is_visible` waits on the very loop running the hook, so the shell records what it did.
#[cfg(partner_pet)]
static PET_WINDOW_SHOWN: AtomicBool = AtomicBool::new(false);

#[cfg(partner_pet)]
fn create_pet_window(app: &AppHandle, enabled_top: bool) -> Result<(), String> {
    if let Some(window) = app.get_webview_window(PET_LABEL) {
        // A window that exists but is hidden must come back rather than be a silent no-op: the preference
        // says it is showing, so anything else leaves the switch and the screen disagreeing.
        let _ = window.show();
        let _ = window.set_always_on_top(enabled_top);
        PET_WINDOW_SHOWN.store(true, Ordering::SeqCst);
        emit_pet_visible(app, true);
        return Ok(());
    }
    let script = pet_script(app)?;
    let monitors = desktop_monitors(app);

    let window = WebviewWindowBuilder::new(app, PET_LABEL, WebviewUrl::App("pet.html".into()))
        .title("Partner")
        // A desktop pet is a sprite on the desktop: no frame, no shadow, no taskbar entry, and never the
        // window that steals focus when it opens.
        .transparent(true)
        .decorations(false)
        .shadow(false)
        .always_on_top(enabled_top)
        .skip_taskbar(true)
        .resizable(false)
        .focused(false)
        // Sized and positioned while hidden, so the restored geometry never flashes at the default spot first.
        .visible(false)
        .inner_size(
            f64::from(PetRect::DEFAULT_WIDTH),
            f64::from(PetRect::DEFAULT_HEIGHT),
        )
        .initialization_script(&script)
        // The page starts its ticker and cursor poll as soon as it loads; it has to be told whether this
        // window is on screen before that, and a page load is the only moment the webview can hear it. A
        // reload of a window that is already up gets `true` again.
        .on_page_load(|window, payload| {
            if payload.event() == PageLoadEvent::Finished {
                let visible = PET_WINDOW_SHOWN.load(Ordering::SeqCst);
                emit_pet_visible(window.app_handle(), visible);
            }
        })
        .build()
        .map_err(|error| error.to_string())?;

    match read_pet_geometry() {
        Some(geometry) => {
            // The file is physical pixels, because that is what the window reports back; a logical set_size
            // here would drift by the display scale factor on every restore.
            let rect = restore_pet_rect(geometry, &monitors);
            let _ = window.set_size(PhysicalSize::new(rect.width, rect.height));
            let _ = window.set_position(PhysicalPosition::new(rect.x, rect.y));
        }
        None => {
            // A fresh window is 300x360 logical, which the window has already resolved to physical pixels.
            if let (Some(primary), Ok(size)) = (monitors.first(), window.outer_size()) {
                let rect = primary.park(PetRect {
                    x: 0,
                    y: 0,
                    width: size.width,
                    height: size.height,
                });
                let _ = window.set_position(PhysicalPosition::new(rect.x, rect.y));
            }
        }
    }

    // Before `show`: a page that finishes loading right after must hear `true`, not the hidden default.
    PET_WINDOW_SHOWN.store(true, Ordering::SeqCst);
    let _ = window.show();
    emit_pet_visible(app, true);
    Ok(())
}

/// Tells the pet page whether its window is on screen, so it can stop its ticker and cursor poll while hidden.
/// Tauri has no visibility event, so the shell emits the transitions it performs and re-states the current state on
/// every page load.
#[cfg(partner_pet)]
fn emit_pet_visible(app: &AppHandle, visible: bool) {
    let _ = app.emit_to(EventTarget::webview_window(PET_LABEL), PET_EVENT, visible);
}

#[cfg(partner_pet)]
fn destroy_pet_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window(PET_LABEL) {
        let _ = window.destroy();
    }
    PET_WINDOW_SHOWN.store(false, Ordering::SeqCst);
}

#[cfg(partner_pet)]
fn remember_pet_geometry(window: &tauri::Window) {
    let (Ok(position), Ok(size)) = (window.outer_position(), window.outer_size()) else {
        return;
    };
    let rect = PetRect {
        x: position.x,
        y: position.y,
        width: size.width,
        height: size.height,
    };
    queue_pet_rect(PetGeometry {
        rect,
        // Which display the rect's centre is on. `monitor_from_point` is the window's own answer, so it already
        // accounts for a display whose origin is negative (a monitor arranged left of the primary one).
        monitor: window
            .monitor_from_point(
                f64::from(rect.x) + f64::from(rect.width) / 2.0,
                f64::from(rect.y) + f64::from(rect.height) / 2.0,
            )
            .ok()
            .flatten()
            .and_then(|monitor| monitor.name().cloned()),
    });
}

/// Serializes the window build/destroy. Two toggles fired at once (tray click plus a settings save) would both
/// see "no window" and both build one; the second `build` then fails on the duplicate label. Holding this for
/// the whole decision-and-act step makes the second call observe the first one's result instead.
#[cfg(partner_pet)]
static PET_WINDOW_GATE: Mutex<()> = Mutex::new(());

/// The one place the preference and the window are changed together, shared by the command and the tray item.
///
/// The window work runs on a plain thread (`spawn_blocking`) that the caller awaits: Tauri documents creating a
/// window from a separate thread, and it keeps a webview build or destroy off both the event loop and the tokio
/// workers.
#[cfg(partner_pet)]
async fn set_pet_preference(app: &AppHandle, enabled: bool) -> Result<(), String> {
    let handle = app.clone();
    let joined = tauri::async_runtime::spawn_blocking(move || {
        let _guard = match PET_WINDOW_GATE.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        if enabled {
            create_pet_window(&handle, true)?;
        } else {
            destroy_pet_window(&handle);
        }
        // Written under the same gate: a later toggle must see this value or overwrite it, never interleave.
        write_pet_pref(enabled)
    })
    .await;

    match joined {
        Ok(result) => result,
        Err(_) => Err("the pet window task stopped without an answer".to_string()),
    }
}

#[cfg(partner_pet)]
fn pet_window(app: &AppHandle) -> Result<tauri::WebviewWindow, String> {
    app.get_webview_window(PET_LABEL)
        .ok_or_else(|| "the pet window is not open".to_string())
}

/// The cursor report the pet window's hit test and eye tracking run on.
#[cfg(partner_pet)]
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PetCursorReport {
    cursor_x: i32,
    cursor_y: i32,
    inside: bool,
    width: u32,
    height: u32,
    scale: f64,
}

#[cfg(partner_pet)]
#[tauri::command]
fn get_pet_enabled(caller: tauri::WebviewWindow) -> Result<bool, String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    Ok(read_pet_pref())
}

#[cfg(partner_pet)]
#[tauri::command]
async fn set_pet_enabled(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    enabled: bool,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    // Async on purpose, like every Tauri command that builds a webview: a synchronous command runs on the
    // event-loop thread, and building or destroying a webview from there waits for the very loop that is waiting
    // for this call to return. It does not fail — it stops answering, taking every other command with it.
    set_pet_preference(&app, enabled).await?;
    // The tray item and this command are one switch; the settings page listens for either.
    let _ = app.emit_to(MAIN_LABEL, PET_EVENT, enabled);
    Ok(())
}

#[cfg(partner_pet)]
#[tauri::command]
fn pet_set_passthrough(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    enabled: bool,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL, PET_LABEL])?;
    pet_window(&app)?
        .set_ignore_cursor_events(enabled)
        .map_err(|error| error.to_string())
}

#[cfg(partner_pet)]
#[tauri::command]
fn pet_cursor(caller: tauri::WebviewWindow, app: AppHandle) -> Result<PetCursorReport, String> {
    require_caller(&caller, &[MAIN_LABEL, PET_LABEL])?;
    let window = pet_window(&app)?;
    let cursor = app.cursor_position().map_err(|error| error.to_string())?;
    let position = window.outer_position().map_err(|error| error.to_string())?;
    let size = window.outer_size().map_err(|error| error.to_string())?;
    let scale = window.scale_factor().map_err(|error| error.to_string())?;

    // Physical pixels: the hit test compares the cursor against the rendered sprite's alpha, which is in
    // device pixels on this machine's screen, so the webview never has to convert units itself.
    let cursor_x = (cursor.x - f64::from(position.x)).round() as i32;
    let cursor_y = (cursor.y - f64::from(position.y)).round() as i32;
    let inside = cursor_x > 0
        && cursor_y > 0
        && cursor_x < size.width as i32
        && cursor_y < size.height as i32;

    Ok(PetCursorReport {
        cursor_x,
        cursor_y,
        inside,
        width: size.width,
        height: size.height,
        scale,
    })
}

#[cfg(partner_pet)]
#[tauri::command]
fn pet_move_by(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    dx: i32,
    dy: i32,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL, PET_LABEL])?;
    let window = pet_window(&app)?;
    let position = window.outer_position().map_err(|error| error.to_string())?;
    window
        .set_position(PhysicalPosition::new(
            position.x.saturating_add(dx),
            position.y.saturating_add(dy),
        ))
        .map_err(|error| error.to_string())
}

/// `width`/`height` are logical pixels: the model reports its canvas size in CSS units, scaled by the user's
/// setting, and the webview's device-pixel ratio is not something the scene has to know about.
#[cfg(partner_pet)]
#[tauri::command]
fn pet_set_size(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    width: f64,
    height: f64,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL, PET_LABEL])?;
    pet_window(&app)?
        .set_size(LogicalSize::new(width, height))
        .map_err(|error| error.to_string())
}

#[cfg(partner_pet)]
#[tauri::command]
fn pet_always_on_top(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    enabled: bool,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL, PET_LABEL])?;
    pet_window(&app)?
        .set_always_on_top(enabled)
        .map_err(|error| error.to_string())
}

/// "Reset position" in the settings page: the window returns to where a fresh one opens, and the file is
/// dropped so a launch that never moves the window again does not restore the old spot.
#[cfg(partner_pet)]
#[tauri::command]
fn pet_reset_position(caller: tauri::WebviewWindow, app: AppHandle) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL, PET_LABEL])?;
    if let Some(path) = pet_rect_path() {
        let _ = std::fs::remove_file(path);
    }

    let window = pet_window(&app)?;
    let size = window.outer_size().map_err(|error| error.to_string())?;
    let monitors = desktop_monitors(&app);
    let current = PetRect {
        x: 0,
        y: 0,
        width: size.width,
        height: size.height,
    };
    // Parked on the primary display, then run through the same clamp as a restored rect: a display whose
    // origin is negative can put the parked corner off-screen, and the clamp is the one place that decides.
    let rect = match monitors.first() {
        Some(primary) => clamp_pet_rect(primary.park(current), &monitors),
        None => current,
    };
    window
        .set_position(PhysicalPosition::new(rect.x, rect.y))
        .map_err(|error| error.to_string())
}

/// `/UPDATE` installs over the previous version instead of running its uninstaller, whose page defaults to
/// uninstalling first; `/P` skips that page and the directory page, so the recorded install directory is reused.
const UPDATE_ARGS: [&str; 2] = ["/UPDATE", "/P"];

/// The installer command, kept separate so the switches are a value a test can read.
fn installer_command(installer: &Path) -> Command {
    let mut command = Command::new(installer);
    command.args(UPDATE_ARGS);
    command
}

/// Starts the verified installer and quits, because the installer replaces the files this process runs from.
///
/// The path comes from the API's download state, but it is re-checked here: this is the step that executes a
/// downloaded file, so a path that is not an existing `.exe` must never reach `Command`.
#[tauri::command]
fn install_update(
    caller: tauri::WebviewWindow,
    app: AppHandle,
    path: String,
) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    let installer = PathBuf::from(&path);
    let is_exe = installer
        .extension()
        .is_some_and(|extension| extension.eq_ignore_ascii_case("exe"));
    if !installer.is_file() || !is_exe {
        return Err(format!("not an installer: {path}"));
    }

    installer_command(&installer)
        .spawn()
        .map_err(|error| format!("could not start the installer: {error}"))?;

    // The installer overwrites api/*, and Windows will not replace a running image.
    stop_sidecar(&app);

    // The installer replaces this executable, so the shell has to go. Let the installer start first: its own
    // check for a running instance then sees a process already on its way out instead of prompting the person
    // to close the app that just told them it was updating.
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(1200));
        app.exit(0);
    });

    Ok(())
}

/// Stops the sidecar and keeps the supervisor from restarting it. Shared by the update hand-off and the exit hook.
fn stop_sidecar(app: &AppHandle) {
    let Some(state) = app.try_state::<Sidecar>() else {
        return;
    };
    // Mark the stop before killing the child so the supervisor does not restart it.
    state.stopping.store(true, Ordering::SeqCst);
    if let Ok(mut guard) = state.child.lock() {
        if let Some(mut child) = guard.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    };
}

pub fn run() {
    let builder = tauri::Builder::default()
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
            let child = start_api(port, &token)?;

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

            app.manage(Sidecar {
                child: Mutex::new(Some(child)),
                stopping: AtomicBool::new(false),
            });

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

            supervise_sidecar(app.handle().clone(), port, token);
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
        get_tray_enabled,
        set_tray_enabled,
        install_update,
        get_pet_enabled,
        set_pet_enabled,
        pet_set_passthrough,
        pet_cursor,
        pet_move_by,
        pet_set_size,
        pet_always_on_top,
        pet_reset_position
    ]);
    #[cfg(not(partner_pet))]
    let builder = builder.invoke_handler(tauri::generate_handler![
        get_tray_enabled,
        set_tray_enabled,
        install_update
    ]);

    builder
        .build(tauri::generate_context!())
        .expect("failed to build the shell")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                stop_sidecar(app);
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

    /// A rect left by a session whose display is gone must come back on screen, at the same size, parked inside the
    /// primary monitor's work area — above the taskbar, not behind it.
    #[cfg(partner_pet)]
    #[test]
    fn a_pet_rect_off_every_monitor_is_parked_on_the_primary_one() {
        // A 1920x1080 display with the usual 40px taskbar along the bottom: the full height is 1080, the work
        // area ends at 1040.
        let primary = MonitorRect {
            name: Some("\\\\.\\DISPLAY1".to_string()),
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
            work_x: 0,
            work_y: 0,
            work_width: 1920,
            work_height: 1040,
        };
        let secondary = MonitorRect {
            name: Some("\\\\.\\DISPLAY2".to_string()),
            x: 1920,
            y: 0,
            width: 1280,
            height: 1024,
            work_x: 1920,
            work_y: 0,
            work_width: 1280,
            work_height: 1024,
        };
        let stale = PetRect {
            x: 4200,
            y: 300,
            width: 300,
            height: 360,
        };

        let rect = clamp_pet_rect(stale, &[primary, secondary]);

        assert_eq!(rect.width, stale.width);
        assert_eq!(rect.height, stale.height);
        assert_eq!(rect.x, 1920 - 300 - PetRect::SCREEN_MARGIN);
        // The work area's bottom edge, not the screen's: 1040 - 360 - 24. Parking at 1080 would put the pet under
        // the taskbar, where it cannot be clicked or dragged.
        assert_eq!(rect.y, 1040 - 360 - PetRect::SCREEN_MARGIN);
    }

    /// A rect that is on a monitor keeps its exact position, including on a secondary display: the user put it
    /// there, and half-off an edge is a legitimate choice.
    #[cfg(partner_pet)]
    #[test]
    fn a_pet_rect_on_a_monitor_is_untouched() {
        let primary = MonitorRect {
            name: Some("\\\\.\\DISPLAY1".to_string()),
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
            work_x: 0,
            work_y: 0,
            work_width: 1920,
            work_height: 1040,
        };
        let secondary = MonitorRect {
            name: Some("\\\\.\\DISPLAY2".to_string()),
            x: 1920,
            y: -200,
            width: 1280,
            height: 1024,
            work_x: 1920,
            work_y: -200,
            work_width: 1280,
            work_height: 1024,
        };
        let placed = PetRect {
            x: 2300,
            y: -150,
            width: 300,
            height: 360,
        };

        assert_eq!(clamp_pet_rect(placed, &[primary, secondary]), placed);
    }

    /// Two identical displays swap places when one is replugged, so the saved name — not the saved position — is
    /// what decides where the window comes back. Without this the rect is still on *a* monitor and the geometric
    /// clamp leaves it there, on the wrong display.
    #[cfg(partner_pet)]
    #[test]
    fn a_saved_monitor_name_beats_a_position_that_still_fits() {
        // After the swap, DISPLAY2 is the left-hand display and DISPLAY1 the right-hand one.
        let left = MonitorRect {
            name: Some("\\\\.\\DISPLAY2".to_string()),
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
            work_x: 0,
            work_y: 0,
            work_width: 1920,
            work_height: 1040,
        };
        let right = MonitorRect {
            name: Some("\\\\.\\DISPLAY1".to_string()),
            x: 1920,
            y: 0,
            width: 1920,
            height: 1080,
            work_x: 1920,
            work_y: 0,
            work_width: 1920,
            work_height: 1040,
        };
        // Saved while the window sat on the right-hand display — which was DISPLAY2 then and is DISPLAY1 now, so
        // the saved coordinates are still inside a monitor's area and the geometric clamp alone would keep them.
        let geometry = PetGeometry {
            rect: PetRect {
                x: 2600,
                y: 500,
                width: 300,
                height: 360,
            },
            monitor: Some("\\\\.\\DISPLAY2".to_string()),
        };

        let rect = restore_pet_rect(geometry, &[left, right]);

        // DISPLAY2 is now the left monitor, so the window parks at its bottom-right corner, not the old spot.
        assert_eq!(rect.x, 0 + 1920 - 300 - PetRect::SCREEN_MARGIN);
        assert_eq!(rect.y, 1040 - 360 - PetRect::SCREEN_MARGIN);
    }

    /// A monitor that no longer exists falls through to the geometric clamp rather than being trusted blindly.
    #[cfg(partner_pet)]
    #[test]
    fn a_saved_monitor_that_is_gone_falls_back_to_the_clamp() {
        let primary = MonitorRect {
            name: Some("\\\\.\\DISPLAY1".to_string()),
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
            work_x: 0,
            work_y: 0,
            work_width: 1920,
            work_height: 1040,
        };
        let geometry = PetGeometry {
            rect: PetRect {
                x: 4000,
                y: 300,
                width: 300,
                height: 360,
            },
            monitor: Some("\\\\.\\DISPLAY9".to_string()),
        };

        let rect = restore_pet_rect(geometry, &[primary]);

        assert_eq!(rect.x, 1920 - 300 - PetRect::SCREEN_MARGIN);
    }

    /// The geometry file stays a flat integer object with one optional quoted name, and a round trip reproduces it.
    #[cfg(partner_pet)]
    #[test]
    fn the_geometry_file_round_trips_a_monitor_name() {
        let geometry = PetGeometry {
            rect: PetRect {
                x: -1200,
                y: 40,
                width: 300,
                height: 360,
            },
            monitor: Some("\\\\.\\DISPLAY2".to_string()),
        };
        let text = format!(
            "{{\"x\":{},\"y\":{},\"width\":{},\"height\":{},\"monitor\":{}}}",
            geometry.rect.x,
            geometry.rect.y,
            geometry.rect.width,
            geometry.rect.height,
            json_quote("\\\\.\\DISPLAY2"),
        );

        assert_eq!(decode_pet_rect(&text), Some(geometry.rect));
        assert_eq!(
            json_string(&text, "monitor").as_deref(),
            Some("\\\\.\\DISPLAY2")
        );
    }

    /// No monitor information is not a reason to move a window the user positioned.
    #[cfg(partner_pet)]
    #[test]
    fn without_monitors_the_pet_rect_is_unchanged() {
        let rect = PetRect {
            x: 40,
            y: 60,
            width: 300,
            height: 360,
        };

        assert_eq!(clamp_pet_rect(rect, &[]), rect);
    }
}
