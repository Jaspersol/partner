using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using Partner.Api.Settings;

namespace Partner.Api.Voice;

/// <summary>One managed local engine's observable state.</summary>
public sealed record LocalEngineState(bool Running, string Reason, int? ProcessId);

public sealed record LocalEnginesState(LocalEngineState Tts, LocalEngineState Kws);

/// <summary>
/// Starts and stops the local voice engines (TTS server, KWS/ASR server) as child processes. The engine is a
/// separate process, and process exit is the only reliable release point: the ONNX memory pool does not return
/// memory to the host after growing. One executable and port per engine; the switch and command changes drive
/// start/stop.
/// </summary>
public sealed class LocalEngineHost : IAsyncDisposable
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string? _appDir;

    private ManagedEngine? _tts;
    private ManagedEngine? _kws;
    private bool _disposed;

    /// <param name="appDir">Relative command and working-directory paths are resolved against it. The engine module
    /// lives beside the app directory and settings store relative paths, so moving or reinstalling the app keeps
    /// them valid. Null = paths are used as-is.</param>
    public LocalEngineHost(string? appDir = null) => _appDir = appDir;

    public LocalEnginesState State => new(
        _tts?.State ?? new LocalEngineState(false, "未托管", null),
        _kws?.State ?? new LocalEngineState(false, "未托管", null));

    /// <summary>Applies the local section: stops engines whose config changed or disabled, starts the enabled ones.</summary>
    public async Task<LocalEnginesState> ReconfigureAsync(LocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _tts = await ApplyAsync(_tts, settings.Tts, "TTS", _appDir).ConfigureAwait(false);
            _kws = await ApplyAsync(_kws, settings.Kws, "KWS/ASR", _appDir).ConfigureAwait(false);
            if (_kws is { IsRunning: true })
            {
                await PushKeywordsAsync(_kws).ConfigureAwait(false);
            }
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Idempotent convergence: a correctly-running engine is left alone. Keywords are pushed only while
    /// the engine is alive, so after a mid-session restart (Exited → next Reconfigure) _keywordsSynced resets with
    /// the new instance and they are pushed again.</summary>
    private static async Task<ManagedEngine?> ApplyAsync(ManagedEngine? current, LocalEngineSettings wanted, string name, string? appDir)
    {
        // Same config and live process: do nothing — saving settings must not churn a running engine.
        if (current is not null && current.Matches(wanted) && current.IsRunning)
        {
            return current;
        }

        if (current is not null)
        {
            await current.StopAsync(name).ConfigureAwait(false);
        }

        if (!wanted.Enabled || string.IsNullOrWhiteSpace(wanted.Command))
        {
            return wanted.Enabled
                ? ManagedEngine.NotStarted(wanted, "未配置可执行文件")
                : null;
        }

        return await ManagedEngine.StartAsync(wanted, name, appDir).ConfigureAwait(false);
    }

    /// <summary>
    /// Keyword push. Only when the configured Keywords are non-empty — a table swap makes the engine rebuild its
    /// recognition stream (read the file, build the spotter), which is a real action; an empty list means the user
    /// configured none, so the engine's current list is kept rather than silently cleared. Failures are not
    /// thrown: an unsynced keyword list must not 500 the settings save, the engine still shows as running, and the
    /// next config change retries.
    /// </summary>
    private static async Task PushKeywordsAsync(ManagedEngine engine)
    {
        var keywords = engine.SettingsSnapshot.Keywords;
        if (keywords.Count == 0 || engine.KeywordsSynced)
        {
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            // The engine endpoint expects a {"keywords":[…]} wrapper (same shape as its GET), not a bare array.
            // Serialize UTF-8 bytes directly: PutAsJsonAsync's formatter layer produced an empty {} body on a real
            // machine, so the engine saw an empty keyword list and the push never landed.
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["keywords"] = keywords });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PutAsync(
                $"http://127.0.0.1:{engine.SettingsSnapshot.Port}/keywords",
                content).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                engine.KeywordsSynced = true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"LocalEngineHost: KWS 词表下发失败 HTTP {(int)response.StatusCode} {body}");
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"LocalEngineHost: KWS 词表下发失败 {error.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await (_tts?.StopAsync("TTS") ?? Task.CompletedTask).ConfigureAwait(false);
            await (_kws?.StopAsync("KWS/ASR") ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>One engine's process + the settings it was started with.</summary>
    private sealed class ManagedEngine
    {
        private readonly LocalEngineSettings _settings;
        private Process? _process;
        private EngineJob? _job;

        /// <summary>Keywords were pushed successfully during this process's lifetime; prevents rebuilding the
        /// recognition stream on every Reconfigure.</summary>
        public bool KeywordsSynced;

        /// <summary>The settings this engine was started with; keyword push reads their Keywords and Port.</summary>
        public LocalEngineSettings SettingsSnapshot => _settings;

        private ManagedEngine(LocalEngineSettings settings, LocalEngineState state)
        {
            _settings = settings;
            State = state;
        }

        public LocalEngineState State { get; private set; }

        public bool IsRunning
        {
            get
            {
                var process = _process;
                if (process is null)
                {
                    return false;
                }

                try
                {
                    return !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        private static string? Resolve(string? appDir, string? path) =>
            appDir is null ? path : EnginePackage.ResolveStored(appDir, path);

        /// <summary>
        /// The default interpreter is the relocatable <c>python/python.exe</c> shipped in the engine package. On a
        /// dev machine that folder does not exist and the engine lives in a venv instead; fall back to it rather
        /// than failing to spawn the process.
        /// </summary>
        private static string? ResolveCommand(string? appDir, string? path)
        {
            var resolved = Resolve(appDir, path);
            if (appDir is null || string.IsNullOrWhiteSpace(resolved) || File.Exists(resolved))
            {
                return resolved;
            }

            return EnginePackage.FallbackInterpreter(resolved) ?? resolved;
        }

        public bool Matches(LocalEngineSettings other) => other.Equals(_settings);

        /// <summary>An engine that is wanted but unreachable without a command (used for the state report).</summary>
        public static ManagedEngine NotStarted(LocalEngineSettings settings, string reason) =>
            new(settings, new LocalEngineState(false, reason, null));

        public static async Task<ManagedEngine> StartAsync(LocalEngineSettings settings, string name, string? appDir)
        {
            var engine = new ManagedEngine(settings, new LocalEngineState(false, "启动中", null));
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveCommand(appDir, settings.Command) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Resolve(appDir, settings.WorkingDirectory) ?? string.Empty,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            foreach (var argument in settings.Arguments)
            {
                // Port placeholder: one template feeds both the engine's listen port and the host's connect port.
                startInfo.ArgumentList.Add(argument.Replace("{port}", settings.Port.ToString()));
            }

            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    engine.State = new LocalEngineState(false, $"启动失败（Start 返回 false）", null);
                    return engine;
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                process.Dispose();
                engine.State = new LocalEngineState(false, $"启动失败：{error.Message}", null);
                return engine;
            }

            engine._process = process;
            // The job object is the "app exit = engine exit" backstop: engine GPU/RAM is released only on process
            // exit, and if the API crashes or is hard-killed, DisposeAsync never runs — so the kernel must step in.
            // The job handle dies with this process, killing the whole tree.
            engine._job = EngineJob.Create(process, name);
            var pid = process.Id;

            // An engine crash must report a reason: exit reports the exit code, only survival counts as running.
            // Readiness returns early, though — an engine up in 1 s returns in 1 s instead of always sleeping the
            // full ReadyTimeoutSeconds (default 30 s; the two engines start serially).
            var grace = TimeSpan.FromSeconds(Math.Clamp(settings.ReadyTimeoutSeconds, 1, 30));
            var ready = await WaitUntilReadyAsync(process, settings.Port, grace).ConfigureAwait(false);
            engine.State = ready
                ? new LocalEngineState(true, "运行中", pid)
                : new LocalEngineState(false, $"进程已退出（代码 {process.ExitCode}）", null);

            // A process that dies later in the session must be visible too. The exit event clears the state's
            // liveness without blocking the calling thread.
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => engine.State = new LocalEngineState(false, $"进程已退出（代码 {engine.SafeExitCode()}）", null);

            return engine;
        }

        /// <summary>
        /// Waits for readiness: polls /health (2xx = ready) and returns early; a crashed process returns false
        /// immediately; the bound is the configured grace. With no port there is nothing to probe, so it degrades
        /// to sleeping the full grace and only checking the process did not exit.
        /// </summary>
        private static async Task<bool> WaitUntilReadyAsync(Process process, int port, TimeSpan timeout)
        {
            if (port <= 0)
            {
                await Task.Delay(timeout).ConfigureAwait(false);
                return !process.HasExited;
            }

            var deadline = Stopwatch.StartNew();
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromMilliseconds(300),
            };

            while (deadline.Elapsed < timeout)
            {
                if (process.HasExited)
                {
                    return false;
                }

                try
                {
                    using var response = await http.GetAsync("health").ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
                {
                    // Not listening / not ready yet; keep polling.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }

            // Still no /health at the bound: if the process is alive, report running rather than falsely claim a crash.
            return !process.HasExited;
        }

        public async Task StopAsync(string name)
        {
            var process = _process;
            _process = null;
            if (process is null)
            {
                return;
            }

            try
            {
                // Belt and braces: give the Exit event a moment to happen naturally, then kill the whole tree.
                // Local engines (sherpa / tts-server) often tolerate stdin closing and must be killed hard.
                if (!await Task.Run(() => process.WaitForExit((int)StopGrace.TotalMilliseconds)).ConfigureAwait(false))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already exited or cannot be waited on; nothing more to do.
            }

            _job?.Dispose();
            _job = null;
            process.Dispose();
            State = new LocalEngineState(false, "已停止", null);
        }

        private int SafeExitCode()
        {
            try
            {
                return _process?.HasExited == true ? _process.ExitCode : -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }
}
