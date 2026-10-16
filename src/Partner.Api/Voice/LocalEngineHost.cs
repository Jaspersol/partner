using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
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

    /// <summary>Idempotent convergence: a correctly-running engine is left alone. 词表在引擎活着时才推，
    /// 所以引擎中途重启（Exited→下次 Reconfigure 重拉）后 _keywordsSynced 随新实例归零，会重新下发。</summary>
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

    /// <summary>
    /// 词表下发。配置里的 Keywords 非空才推——引擎端ী换表要重建识别流（读文件、建 spotter），是实质动作；
    /// 空表 = 用户没配 = 保留引擎当前的词表，不被宿主静默清空。失败不抛：词表没同步上不该让设置保存
    /// 整个 500，状态里已能看到引擎在跑；下次改任意配置会再推。
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
            // 引擎端点吃 {"keywords":[…]} 包装对象（和它的 GET 返回同形），不是裸数组。
            // 手拼 UTF-8 字节直发：PutAsJsonAsync 的 formatter 层在真实机上把 body 变成 {}，
            // 引擎读出来是空词表、词推不上去。
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

        /// <summary>词表已在本次进程生命周期里成功下发过；防止每次 Reconfigure 都重建一遍识别流。</summary>
        public bool KeywordsSynced;

        /// <summary>启动它时用的那份配置；词表下发要读它的 Keywords 与 Port。</summary>
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
            // 作业对象是「应用退出=引擎退出」的兜底：引擎的显存/内存只在进程退出时释放（本地音频.md），
            // 若 API 崩溃或被硬杀走不到 DisposeAsync，内核就必须代劳——作业句柄随本进程消亡，整树被杀。
            engine._job = EngineJob.Create(process, name);
            var pid = process.Id;

            // 引擎进程闪退必须有理由：退出即报进程码，活下来才算「运行中」。但就绪要早退——引擎 1s
            // 起好就 1s 返回，不再固定睡满 ReadyTimeoutSeconds（默认 30s，两引擎串行曾是启动的主瓶颈）。
            var grace = TimeSpan.FromSeconds(Math.Clamp(settings.ReadyTimeoutSeconds, 1, 30));
            var ready = await WaitUntilReadyAsync(process, settings.Port, grace).ConfigureAwait(false);
            engine.State = ready
                ? new LocalEngineState(true, "运行中", pid)
                : new LocalEngineState(false, $"进程已退出（代码 {process.ExitCode}）", null);

            // But a process that dies during the session must be visible too. 退出事件闭掉 state 的活性，
            // 不阻塞调用线程。
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => engine.State = new LocalEngineState(false, $"进程已退出（代码 {engine.SafeExitCode()}）", null);

            return engine;
        }

        /// <summary>
        /// 等引擎就绪：轮询 /health（2xx 即就绪）提前返回，进程闪退立即返回 false，上限是配置的宽限。
        /// 端口没配就无从探测，退化为「睡满宽限只验证没闪退」的旧行为。
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
                    // 还没监听/还没就绪，继续轮询。
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }

            // 到上限仍未见 /health：进程活着就按「运行中」报，宁可让后续请求吃重试，也不假报闪退。
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
