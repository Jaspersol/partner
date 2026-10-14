using System.ComponentModel;
using System.Diagnostics;
using Partner.Api.Settings;

namespace Partner.Api.Voice;

/// <summary>One managed local engine's observable state.</summary>
public sealed record LocalEngineState(bool Running, string Reason, int? ProcessId);

public sealed record LocalEnginesState(LocalEngineState Tts, LocalEngineState Kws);

/// <summary>
/// Starts and stops the local voice engines (TTS server, KWS/ASR server) as child processes
/// 本地音频.md 的模型：引擎是独立进程，「关闭不占资源」的单位是进程——ONNX 内存池扩张后不归还宿主，
/// 只有 exit 才可靠释放。每个引擎一个可执行文件 + 端口；开关与命令变更驱动启停。
/// </summary>
public sealed class LocalEngineHost : IAsyncDisposable
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ManagedEngine? _tts;
    private ManagedEngine? _kws;
    private bool _disposed;

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

            _tts = await ApplyAsync(_tts, settings.Tts, "TTS").ConfigureAwait(false);
            _kws = await ApplyAsync(_kws, settings.Kws, "KWS/ASR").ConfigureAwait(false);
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Idempotent convergence: a correctly-running engine is left alone.</summary>
    private static async Task<ManagedEngine?> ApplyAsync(ManagedEngine? current, LocalEngineSettings wanted, string name)
    {
        // 同配置且进程活着：什么都不做——设置保存不应抖动已在跑的引擎。
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

        return await ManagedEngine.StartAsync(wanted, name).ConfigureAwait(false);
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

        public bool Matches(LocalEngineSettings other) => other.Equals(_settings);

        /// <summary>An engine that is wanted but unreachable without a command (used for the state report).</summary>
        public static ManagedEngine NotStarted(LocalEngineSettings settings, string reason) =>
            new(settings, new LocalEngineState(false, reason, null));

        public static async Task<ManagedEngine> StartAsync(LocalEngineSettings settings, string name)
        {
            var engine = new ManagedEngine(settings, new LocalEngineState(false, "启动中", null));
            var startInfo = new ProcessStartInfo
            {
                FileName = settings.Command,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = settings.WorkingDirectory ?? string.Empty,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            foreach (var argument in settings.Arguments)
            {
                // 端口占位符：引擎要知道监听哪，宿主要连哪，一个模板同时喂两边。
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
            var pid = process.Id;

            // 引擎进程闪退必须有理由：等一小段，退出即报进程码，活下来才算「运行中」。
            var probe = TimeSpan.FromSeconds(Math.Clamp(settings.ReadyTimeoutSeconds, 1, 30));
            await Task.Delay(probe).ConfigureAwait(false);
            engine.State = !process.HasExited
                ? new LocalEngineState(true, "运行中", pid)
                : new LocalEngineState(false, $"进程已退出（代码 {process.ExitCode}）", null);

            // But a process that dies during the session must be visible too. 退出事件闭掉 state 的活性，
            // 不阻塞调用线程。
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => engine.State = new LocalEngineState(false, $"进程已退出（代码 {engine.SafeExitCode()}）", null);

            return engine;
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
                // 双保险：先给 Exit 事件一点时间自然结束，超时杀整棵树——
                // 本地引擎（sherpa / tts-server）经常可以容忍 stdin 关闭而不退，必须硬杀。
                if (!await Task.Run(() => process.WaitForExit((int)StopGrace.TotalMilliseconds)).ConfigureAwait(false))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // 已经退出或无法等待；无需再处理。
            }

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
