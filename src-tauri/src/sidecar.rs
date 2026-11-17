use super::*;
/// Keeps the sidecar handle and marks intentional stops so the supervisor does not restart it.
pub(crate) struct Sidecar {
    child: Mutex<Option<Child>>,
    stopping: AtomicBool,
    generation: std::sync::atomic::AtomicU64,
    port: u16,
    token: String,
    #[cfg(windows)]
    job: Job,
    diagnostic: Mutex<SidecarDiagnostic>,
    retry: AtomicBool,
}

impl Sidecar {
    pub(crate) fn new(port: u16, token: String) -> Result<Self, String> {
        Ok(Self { child: Mutex::new(None), stopping: AtomicBool::new(false),
            generation: std::sync::atomic::AtomicU64::new(0), port, token,
            #[cfg(windows)]
            job: Job::new()?,
            diagnostic: Mutex::new(SidecarDiagnostic { error: String::new(), exit_code: None, failures: 0, log_path: api_log_path().map(|p| p.display().to_string()) }),
            retry: AtomicBool::new(false) })
    }
}

#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SidecarDiagnostic {
    error: String,
    exit_code: Option<i32>,
    failures: u32,
    log_path: Option<String>,
}

#[cfg(windows)]
struct Job(windows_sys::Win32::Foundation::HANDLE);
#[cfg(windows)]
unsafe impl Send for Job {}
#[cfg(windows)]
unsafe impl Sync for Job {}
#[cfg(windows)]
impl Job {
    fn new() -> Result<Self, String> {
        use windows_sys::Win32::System::JobObjects::*;
        unsafe {
            let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if handle.is_null() { return Err(std::io::Error::last_os_error().to_string()); }
            let job = Self(handle);
            let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if SetInformationJobObject(handle, JobObjectExtendedLimitInformation, &info as *const _ as _, std::mem::size_of_val(&info) as u32) == 0 {
                return Err(std::io::Error::last_os_error().to_string());
            }
            Ok(job)
        }
    }
    fn assign(&self, child: &Child) -> Result<(), String> {
        let ok = unsafe { windows_sys::Win32::System::JobObjects::AssignProcessToJobObject(self.0, child.as_raw_handle() as _) };
        if ok == 0 { Err(std::io::Error::last_os_error().to_string()) } else { Ok(()) }
    }
    fn terminate(&self) -> Result<(), String> {
        use windows_sys::Win32::System::JobObjects::*;
        if unsafe { TerminateJobObject(self.0, 1) } == 0 {
            return Err(format!("could not terminate sidecar job: {}", std::io::Error::last_os_error()));
        }
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            let mut info: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION = unsafe { std::mem::zeroed() };
            if unsafe { QueryInformationJobObject(self.0, JobObjectBasicAccountingInformation, &mut info as *mut _ as _, std::mem::size_of_val(&info) as u32, std::ptr::null_mut()) } == 0 {
                return Err(format!("could not query sidecar job: {}", std::io::Error::last_os_error()));
            }
            if info.ActiveProcesses == 0 { return Ok(()); }
            if Instant::now() >= deadline { return Err("sidecar job did not terminate within 10 seconds".into()); }
            std::thread::sleep(Duration::from_millis(50));
        }
    }
}
#[cfg(windows)]
impl Drop for Job {
    fn drop(&mut self) { unsafe { windows_sys::Win32::Foundation::CloseHandle(self.0); } }
}

#[tauri::command]
pub(crate) fn sidecar_diagnostic(caller: tauri::WebviewWindow, state: State<'_, Sidecar>) -> Result<SidecarDiagnostic, String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    Ok(state.diagnostic.lock().unwrap_or_else(|e| e.into_inner()).clone())
}

#[tauri::command]
pub(crate) fn retry_sidecar(caller: tauri::WebviewWindow, state: State<'_, Sidecar>) -> Result<(), String> {
    require_caller(&caller, &[MAIN_LABEL])?;
    state.retry.store(true, Ordering::SeqCst);
    Ok(())
}

fn record_failure(state: &Sidecar, error: String, exit_code: Option<i32>) {
    append_api_log("shell", &error);
    let mut diagnostic = state.diagnostic.lock().unwrap_or_else(|e| e.into_inner());
    diagnostic.error = error;
    diagnostic.exit_code = exit_code;
    diagnostic.failures = diagnostic.failures.saturating_add(1);
}

const MAX_LOG_BYTES: u64 = 10 * 1024 * 1024;
static LOG_GATE: Mutex<()> = Mutex::new(());

fn api_log_path() -> Option<PathBuf> {
    Some(app_data_dir()?.join("partner-api.log"))
}

pub(crate) fn append_api_log(stream: &str, line: &str) {
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
pub(crate) fn append_log_file(path: &std::path::Path, cap: u64, stream: &str, line: &str) {
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
/// Stops the sidecar and keeps the supervisor from restarting it. Shared by the update hand-off and the exit hook.
pub(crate) fn stop_sidecar(app: &AppHandle) -> Option<(u64, Result<(), String>)> {
    let state = app.try_state::<Sidecar>()?;
    Some(stop_state(&state))
}

fn stop_state(state: &Sidecar) -> (u64, Result<(), String>) {
    let mut guard = state.child.lock().unwrap_or_else(|e| e.into_inner());
    state.stopping.store(true, Ordering::SeqCst);
    let generation = state.generation.fetch_add(1, Ordering::SeqCst) + 1;
    let mut child_error = None;
    if let Some(mut child) = guard.take() {
        drop(child.stdin.take());
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            match child.try_wait() {
                Ok(Some(_)) => break,
                Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(50)),
                _ => {
                    if let Err(error) = child.kill() {
                        if !matches!(child.try_wait(), Ok(Some(_))) {
                            child_error = Some(format!("could not kill sidecar: {error}"));
                        }
                    } else if let Err(error) = child.wait() {
                        child_error = Some(format!("could not wait for sidecar: {error}"));
                    }
                    break;
                }
            }
        }
    }
    let result = {
        #[cfg(windows)]
        { state.job.terminate() }
        #[cfg(not(windows))]
        { Ok(()) }
    };
    (generation, result.and_then(|()| child_error.map_or(Ok(()), Err)))
}

/// Restore supervision if starting the installer failed, unless a later exit superseded this stop.
pub(crate) fn resume_sidecar(app: &AppHandle, generation: u64) {
    let Some(state) = app.try_state::<Sidecar>() else { return; };
    let Some(guard) = resume_state(&state, generation) else { return; };
    supervise_sidecar(app.clone(), state.port, state.token.clone(), generation);
    drop(guard);
}

fn resume_state(state: &Sidecar, generation: u64) -> Option<std::sync::MutexGuard<'_, Option<Child>>> {
    let guard = state.child.lock().unwrap_or_else(|e| e.into_inner());
    if state.generation.load(Ordering::SeqCst) != generation || !state.stopping.load(Ordering::SeqCst) { return None; }
    state.stopping.store(false, Ordering::SeqCst);
    Some(guard)
}
/// Asks the OS for an unused port. The listener is dropped immediately; the API binds it moments later.
pub(crate) fn free_port() -> Result<u16, String> {
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

fn start_api(port: u16, token: &str, state: &Sidecar) -> Result<Child, String> {
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
    #[cfg(windows)]
    if let Err(error) = state.job.assign(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(format!("could not assign sidecar to job: {error}"));
    }
    #[cfg(not(windows))]
    let _ = state;

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

struct RestartBackoff {
    delay: Duration,
    started: Option<Instant>,
}

impl RestartBackoff {
    fn new() -> Self {
        Self { delay: Duration::from_millis(500), started: None }
    }

    fn exited(&mut self, now: Instant) {
        if let Some(started) = self.started.take() {
            if now.duration_since(started) >= Duration::from_secs(60) {
                self.delay = Duration::from_millis(500);
            }
        }
    }

    fn spawned(&mut self, now: Instant) { self.started = Some(now); }

    fn attempted(&mut self) { self.delay = (self.delay * 2).min(Duration::from_secs(30)); }
}

#[derive(Debug, PartialEq, Eq)]
enum RestartWait { Wait, Start, Stop }

fn restart_wait(now: Instant, until: Instant, retry: bool, stopping: bool) -> RestartWait {
    if stopping { RestartWait::Stop }
    else if retry || now >= until { RestartWait::Start }
    else { RestartWait::Wait }
}

/// Restarts the sidecar after abnormal exits; the port and token stay fixed for the loaded webview.
pub(crate) fn supervise_sidecar(app: AppHandle, port: u16, token: String, generation: u64) {
    std::thread::spawn(move || {
        let mut backoff = RestartBackoff::new();

        loop {
            std::thread::sleep(Duration::from_millis(500));

            let Some(state) = app.try_state::<Sidecar>() else {
                return;
            };
            if state.stopping.load(Ordering::SeqCst) || state.generation.load(Ordering::SeqCst) != generation {
                return;
            }

            let exited: Option<Option<ExitStatus>> = {
                let mut guard = match state.child.lock() {
                    Ok(guard) => guard,
                    Err(poisoned) => poisoned.into_inner(),
                };
                if state.stopping.load(Ordering::SeqCst) || state.generation.load(Ordering::SeqCst) != generation { return; }
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

            if state.stopping.load(Ordering::SeqCst) || state.generation.load(Ordering::SeqCst) != generation {
                return;
            }

            // Only an actual child that ran for a full minute resets the backoff.
            backoff.exited(Instant::now());

            if let Some(status) = status {
                record_failure(&state, format!("sidecar exited (code {:?})", status.code()), status.code());
            }

            let until = Instant::now() + backoff.delay;
            loop {
                let stopping = state.stopping.load(Ordering::SeqCst) || state.generation.load(Ordering::SeqCst) != generation;
                match restart_wait(Instant::now(), until, state.retry.swap(false, Ordering::SeqCst), stopping) {
                    RestartWait::Stop => return,
                    RestartWait::Start => break,
                    RestartWait::Wait => std::thread::sleep(Duration::from_millis(100)),
                }
            }
            let mut guard = state.child.lock().unwrap_or_else(|e| e.into_inner());
            if state.stopping.load(Ordering::SeqCst) || state.generation.load(Ordering::SeqCst) != generation { return; }
            match start_api(port, &token, &state) {
                Ok(child) => {
                    *guard = Some(child);
                    backoff.spawned(Instant::now());
                    backoff.attempted();
                    append_api_log("shell", "sidecar started");
                }
                Err(error) => {
                    record_failure(&state, format!("sidecar start failed: {error}"), None);
                    backoff.attempted();
                }
            }
        }
    });
}

/// A fresh bearer token for this launch. Any strong random string works — the API compares the bytes it is
/// given, so the only requirement is that it survives the round-trip through the environment and the
/// initialization script. Hex over URL-safe base64 only because it needs no extra dependency.
pub(crate) fn generate_token() -> String {
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
pub(crate) fn newest_api_build(debug_root: &std::path::Path) -> Option<PathBuf> {
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
    Ok(app_data_dir().ok_or("LOCALAPPDATA is not set")?.join("workspace"))
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;
    use std::sync::{Arc, mpsc};

    #[test]
    fn backoff_only_resets_after_a_stable_child_exits() {
        let mut backoff = RestartBackoff::new();
        let start = Instant::now();
        let mut now = start;
        for _ in 0..12 {
            backoff.exited(now);
            let delay = backoff.delay;
            now += delay;
            backoff.attempted(); // spawn failed
        }
        assert!(now.duration_since(start) > Duration::from_secs(60));
        for _ in 0..3 {
            backoff.exited(now);
            assert_eq!(backoff.delay, Duration::from_secs(30));
            now += backoff.delay;
            backoff.attempted();
        }
        backoff.spawned(now);
        now += Duration::from_secs(59);
        backoff.exited(now);
        assert_eq!(backoff.delay, Duration::from_secs(30));
        backoff.spawned(now);
        now += Duration::from_secs(60);
        backoff.exited(now);
        assert_eq!(backoff.delay, Duration::from_millis(500));
        backoff.attempted();
        backoff.exited(now + Duration::from_secs(120));
        assert_eq!(backoff.delay, Duration::from_secs(1));
    }
    #[test]
    fn retry_and_stop_interrupt_the_backoff_wait() {
        let now = Instant::now();
        let until = now + Duration::from_secs(30);
        assert_eq!(restart_wait(now, until, false, false), RestartWait::Wait);
        assert_eq!(restart_wait(now, until, true, false), RestartWait::Start);
        assert_eq!(restart_wait(now, until, false, true), RestartWait::Stop);
        assert_eq!(restart_wait(now, until, true, true), RestartWait::Stop);
        assert_eq!(restart_wait(until, until, false, false), RestartWait::Start);
    }


    #[test]
    fn failed_update_can_resume_once_but_not_after_a_later_stop() {
        let state = Sidecar::new(1, String::new()).unwrap();
        let (first, result) = stop_state(&state);
        result.unwrap();
        assert!(resume_state(&state, first).is_some());
        assert!(resume_state(&state, first).is_none());
        let (second, result) = stop_state(&state);
        result.unwrap();
        assert!(resume_state(&state, first).is_none());
        assert!(resume_state(&state, second).is_some());
    }

    #[test]
    fn stop_serializes_with_supervisor_publication_and_invalidates_its_generation() {
        let state = Arc::new(Sidecar::new(1, String::new()).unwrap());
        let guard = state.child.lock().unwrap();
        let (tx, rx) = mpsc::channel();
        let stopping = Arc::clone(&state);
        let thread = std::thread::spawn(move || { tx.send(stop_state(&stopping)).unwrap(); });
        assert!(rx.try_recv().is_err());
        drop(guard);
        let (generation, result) = rx.recv().unwrap();
        result.unwrap();
        thread.join().unwrap();
        let _guard = state.child.lock().unwrap();
        assert!(state.stopping.load(Ordering::SeqCst));
        assert_ne!(state.generation.load(Ordering::SeqCst), 0);
        assert_eq!(state.generation.load(Ordering::SeqCst), generation);
    }

    #[cfg(windows)]
    #[test]
    fn job_termination_waits_for_descendant() {
        use windows_sys::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
        use windows_sys::Win32::System::Threading::{OpenProcess, WaitForSingleObject};
        let job = Job::new().unwrap();
        let mut parent = Command::new("powershell.exe")
            .args(["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 1; $i = [System.Diagnostics.ProcessStartInfo]::new('ping.exe', '-t 127.0.0.1'); $i.UseShellExecute = $false; $i.CreateNoWindow = $true; $p = [System.Diagnostics.Process]::Start($i); Write-Output $p.Id; Start-Sleep -Seconds 60"])
            .stdout(Stdio::piped()).spawn().unwrap();
        job.assign(&parent).unwrap();
        let mut line = String::new();
        BufReader::new(parent.stdout.take().unwrap()).read_line(&mut line).unwrap();
        let pid: u32 = line.trim().parse().unwrap();
        let handle = unsafe { OpenProcess(0x0010_0001, 0, pid) }; // SYNCHRONIZE | PROCESS_TERMINATE
        assert!(!handle.is_null());
        job.terminate().unwrap();
        let wait = unsafe { WaitForSingleObject(handle, 5000) };
        if wait != WAIT_OBJECT_0 {
            unsafe { windows_sys::Win32::System::Threading::TerminateProcess(handle, 1); }
        }
        assert_eq!(wait, WAIT_OBJECT_0);
        unsafe { CloseHandle(handle); }
        parent.wait().unwrap();
    }
}
