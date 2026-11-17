use super::*;
// The desktop pet: a second, transparent webview that renders a Live2D model. Everything in this block is behind
// `cfg(partner_pet)` (see build.rs), which is on unless a build sets `PARTNER_PET=0`.

/// The pet window's label, and the key every lookup and event filter uses.
#[cfg(partner_pet)]
pub(crate) const PET_LABEL: &str = "pet";

/// Sent to the `main` window with the resulting boolean whenever the pet is shown or hidden, from whichever
/// switch did it, so the settings page never has to poll the shell.
///
/// The same name is sent to the pet window itself with its own visibility, because that is the one event the
/// page listens for (`ui/src/pet/shellBridge.ts` `onVisibility`); a second name would be silently ignored.
#[cfg(partner_pet)]
pub(crate) const PET_EVENT: &str = "partner:pet";

/// The token and port the webviews are initialized with. The pet window can be created long after startup
/// (from the setting or the tray), so these values have to outlive the `main` window's builder.
#[cfg(partner_pet)]
pub(crate) struct ShellSession {
    pub(crate) port: u16,
    pub(crate) token: String,
}

/// The preference lives beside `tray-enabled` and for the same reason: it is the shell's, and the pet has to
/// come back at startup whether or not the API (which owns the settings document) is answering yet.
#[cfg(partner_pet)]
fn pet_pref_path() -> Option<PathBuf> {
    Some(app_data_dir()?.join("pet-enabled"))
}

#[cfg(partner_pet)]
pub(crate) fn read_pet_pref() -> bool {
    pet_pref_path()
        .and_then(|path| std::fs::read_to_string(path).ok())
        .map(|text| text.trim() == "1")
        .unwrap_or(false)
}

#[cfg(partner_pet)]
pub(crate) fn write_pet_pref(enabled: bool) -> Result<(), String> {
    let path = pet_pref_path().ok_or("LOCALAPPDATA is not set")?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::write(path, if enabled { "1" } else { "0" }).map_err(|error| error.to_string())
}

/// A rectangle in physical pixels relative to the desktop origin — the pet window's outer frame, or a monitor.
#[cfg(partner_pet)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
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
    Some(app_data_dir()?.join("pet-window.json"))
}

/// The geometry file's contents: the window's rect and, when the display reported one, the name of the monitor it
/// sat on.
#[cfg(partner_pet)]
#[derive(serde::Serialize, serde::Deserialize)]
struct PetGeometry {
    #[serde(flatten)]
    rect: PetRect,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    monitor: Option<String>,
}


#[cfg(partner_pet)]
fn read_pet_geometry() -> Option<PetGeometry> {
    read_pet_geometry_at(&pet_rect_path()?)
}

#[cfg(partner_pet)]
fn read_pet_geometry_at(path: &Path) -> Option<PetGeometry> {
    let text = std::fs::read_to_string(path).ok()?;
    let geometry: PetGeometry = serde_json::from_str(&text).ok()?;
    if geometry.rect.width == 0 || geometry.rect.height == 0 {
        return None;
    }
    Some(geometry)
}

#[cfg(partner_pet)]
fn write_pet_rect(geometry: PetGeometry) {
    let Some(path) = pet_rect_path() else {
        return;
    };
    write_pet_rect_at(&path, &geometry);
}

#[cfg(partner_pet)]
fn write_pet_rect_at(path: &Path, geometry: &PetGeometry) {
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Ok(text) = serde_json::to_string(geometry) {
        let _ = std::fs::write(path, text);
    }
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
pub(crate) static PET_WINDOW_SHOWN: AtomicBool = AtomicBool::new(false);

#[cfg(partner_pet)]
pub(crate) fn create_pet_window(app: &AppHandle, enabled_top: bool) -> Result<(), String> {
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
pub(crate) fn destroy_pet_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window(PET_LABEL) {
        let _ = window.destroy();
    }
    PET_WINDOW_SHOWN.store(false, Ordering::SeqCst);
}

#[cfg(partner_pet)]
pub(crate) fn remember_pet_geometry(window: &tauri::Window) {
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
pub(crate) async fn set_pet_preference(app: &AppHandle, enabled: bool) -> Result<(), String> {
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
pub(crate) struct PetCursorReport {
    cursor_x: i32,
    cursor_y: i32,
    inside: bool,
    width: u32,
    height: u32,
    scale: f64,
}

#[cfg(partner_pet)]
#[tauri::command]
pub(crate) fn get_pet_enabled(caller: tauri::WebviewWindow) -> Result<bool, String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    Ok(read_pet_pref())
}

#[cfg(partner_pet)]
#[tauri::command]
pub(crate) async fn set_pet_enabled(
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
pub(crate) fn pet_set_passthrough(
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
pub(crate) fn pet_cursor(caller: tauri::WebviewWindow, app: AppHandle) -> Result<PetCursorReport, String> {
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
pub(crate) fn pet_move_by(
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
pub(crate) fn pet_set_size(
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
pub(crate) fn pet_always_on_top(
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
pub(crate) fn pet_reset_position(caller: tauri::WebviewWindow, app: AppHandle) -> Result<(), String> {
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

#[cfg(test)]
mod tests {
    use super::*;
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
        let path = std::env::temp_dir().join(format!("partner-geometry-{}-{}.json", std::process::id(), generate_token()));
        write_pet_rect_at(&path, &geometry);
        let restored = read_pet_geometry_at(&path).unwrap();
        std::fs::remove_file(path).unwrap();
        assert_eq!(restored.rect, geometry.rect);
        assert_eq!(restored.monitor, geometry.monitor);
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
