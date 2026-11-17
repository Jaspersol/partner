use super::*;
/// `/UPDATE` installs over the previous version instead of running its uninstaller, whose page defaults to
/// uninstalling first; `/P` skips that page and the directory page, so the recorded install directory is reused.
const UPDATE_ARGS: [&str; 2] = ["/UPDATE", "/P"];

/// The installer command, kept separate so the switches are a value a test can read.
pub(crate) fn installer_command(installer: &Path) -> Command {
    let mut command = Command::new(installer);
    command.args(UPDATE_ARGS);
    command
}

/// Starts the verified installer and quits, because the installer replaces the files this process runs from.
///
/// The path comes from the API's download state, but it is re-checked here: this is the step that executes a
/// downloaded file, so a path that is not an existing `.exe` must never reach `Command`.
#[tauri::command]
pub(crate) fn install_update(
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

    // The installer must not start replacing files before the API and its descendants have stopped.
    let Some((generation, result)) = stop_sidecar(&app) else {
        return Err("sidecar state is unavailable".into());
    };
    if let Err(error) = result {
        resume_sidecar(&app, generation);
        return Err(format!("could not stop the sidecar: {error}"));
    }
    if let Err(error) = installer_command(&installer).spawn() {
        resume_sidecar(&app, generation);
        return Err(format!("could not start the installer: {error}"));
    }

    // The installer replaces this executable, so the shell has to go. Let the installer start first: its own
    // check for a running instance then sees a process already on its way out instead of prompting the person
    // to close the app that just told them it was updating.
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(1200));
        app.exit(0);
    });

    Ok(())
}
