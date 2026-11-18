use super::*;
/// Whether closing the window hides it to the tray instead of quitting. Mirrors the file on disk.
pub(crate) struct TrayEnabled(pub(crate) AtomicBool);

/// The preference lives beside the workspace rather than in the API's settings: it is the shell's, and the shell
/// needs it before (and without) the API answering.
fn tray_pref_path() -> Option<PathBuf> {
    Some(app_data_dir()?.join("tray-enabled"))
}

pub(crate) fn read_tray_pref() -> bool {
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

pub(crate) fn show_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
        let _ = window.emit("partner:visibility", true);
    }
}

pub(crate) fn create_tray(app: &AppHandle) -> tauri::Result<()> {
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
            "quit" => {
                // Do not leave the main window visible while the backend finishes graceful shutdown.
                if let Some(window) = app.get_webview_window(MAIN_LABEL) {
                    if let Err(error) = window.hide() {
                        append_api_log("shell", &format!("the main window could not be hidden: {error}"));
                    }
                }
                // Hide the native icon before Exit starts the potentially slow sidecar shutdown. Removing the
                // resource does not change the saved preference, and is harmless if it was already removed.
                if let Some(tray) = app.tray_by_id(TRAY_ID) {
                    if let Err(error) = tray.set_visible(false) {
                        append_api_log("shell", &format!("the tray icon could not be hidden: {error}"));
                    }
                }
                drop(app.remove_tray_by_id(TRAY_ID));
                app.exit(0);
            },
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
pub(crate) fn get_tray_enabled(
    caller: tauri::WebviewWindow,
    state: State<'_, TrayEnabled>,
) -> Result<bool, String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    Ok(state.0.load(Ordering::SeqCst))
}

#[tauri::command]
pub(crate) fn set_tray_enabled(
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
