using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Partner.Api.Auth;
using Partner.Api.Settings;
using Partner.Api.Voice;

namespace Partner.Api.Tests;

/// <summary>
/// Local engine process hosting with real lightweight child processes. Port zero exercises the no-health-probe
/// mode; a configured port requires a successful health response before the engine is ready.
/// </summary>
public sealed class LocalEngineHostTests
{
    private static LocalEngineSettings Engine(int port, bool enabled = true) => new()
    {
        Enabled = enabled,
        Command = "powershell",
        // -NoProfile: loading a profile turns a process that should take milliseconds into seconds.
        // A -Command sleeping 60s has a working set of about 60MB.
        Arguments = new[] { "-NoProfile", "-Command", $"Start-Sleep -Seconds 60; # {port}" },
        Port = 0,
        ReadyTimeoutSeconds = 2,
    };

    [Fact]
    public async Task Enabled_engine_starts_and_reports_running()
    {
        await using var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();

        var state = await host.ReconfigureAsync(new LocalSettings { Tts = Engine(port) });

        Assert.True(state.Tts.Running, state.Tts.Reason);
        Assert.NotNull(state.Tts.ProcessId);
        Assert.Equal("运行中", state.Tts.Reason);
    }

    [Fact]
    public async Task Live_process_without_healthy_endpoint_reports_timeout_not_ready()
    {
        await using var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();
        var state = await host.ReconfigureAsync(new LocalSettings { Tts = Engine(port) with { Port = port, ReadyTimeoutSeconds = 1 } });
        Assert.False(state.Tts.Running);
        Assert.Contains("超时", state.Tts.Reason, StringComparison.Ordinal);
        Assert.NotNull(state.Tts.ProcessId);
    }

    [Fact]
    public async Task Cancelling_warmup_stops_the_started_child()
    {
        await using var host = new LocalEngineHost();
        using var cancel = new CancellationTokenSource();
        var port = ApiFixture.FreePort();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = host.ReconfigureAsync(new LocalSettings { Tts = Engine(port) with { Port = port, ReadyTimeoutSeconds = 30 } }, cancel.Token,
            () => { if (host.State.Tts.ProcessId is int pid) started.TrySetResult(pid); return Task.CompletedTask; });
        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(host.State.Tts.Running);
        await AssertProcessGoneAsync(pid);
    }

    [Fact]
    public async Task Disabled_engine_reports_nothing_and_starts_no_process()
    {
        await using var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();

        var state = await host.ReconfigureAsync(new LocalSettings { Tts = Engine(port, enabled: false) });

        Assert.False(state.Tts.Running);
    }

    [Fact]
    public async Task Disable_stops_a_running_process_for_real()
    {
        await using var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();
        var started = await host.ReconfigureAsync(new LocalSettings { Tts = Engine(port) });
        var pid = started.Tts.ProcessId!.Value;

        var stopped = await host.ReconfigureAsync(
            new LocalSettings { Tts = Engine(port, enabled: false) });

        Assert.False(stopped.Tts.Running);
        await AssertProcessGoneAsync(pid);
    }

    [Fact]
    public async Task Config_change_restarts_the_process()
    {
        await using var host = new LocalEngineHost();
        var firstPort = ApiFixture.FreePort();
        var secondPort = ApiFixture.FreePort();

        var started = await host.ReconfigureAsync(new LocalSettings { Tts = Engine(firstPort) });
        var firstPid = started.Tts.ProcessId!.Value;

        var restarted = await host.ReconfigureAsync(new LocalSettings { Tts = Engine(secondPort) });

        Assert.True(restarted.Tts.Running);
        Assert.NotEqual(firstPid, restarted.Tts.ProcessId);
        await AssertProcessGoneAsync(firstPid);
    }

    [Fact]
    public async Task Saving_the_same_config_again_does_not_restart_the_engine()
    {
        await using var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();
        var settings = new LocalSettings { Tts = Engine(port) };
        var started = await host.ReconfigureAsync(settings);
        var pid = started.Tts.ProcessId!.Value;

        var again = await host.ReconfigureAsync(settings);

        Assert.True(again.Tts.Running);
        Assert.Equal(pid, again.Tts.ProcessId);
    }

    [Fact]
    public async Task Failed_keyword_push_is_retried_without_restarting_the_engine()
    {
        await using var gate = await HealthGate.StartAsync();
        gate.Ready = true;
        gate.FailKeywordPush = true;
        await using var host = new LocalEngineHost();
        var settings = new LocalSettings { Kws = Engine(gate.Port) with { Port = gate.Port, Keywords = ["hello"] } };

        var started = await host.ReconfigureAsync(settings);
        Assert.True(started.Kws.Running, started.Kws.Reason);
        Assert.Equal(1, gate.KeywordPushes);

        gate.FailKeywordPush = false;
        var retried = await host.ReconfigureAsync(settings);
        Assert.Equal(started.Kws.ProcessId, retried.Kws.ProcessId);
        Assert.Equal(2, gate.KeywordPushes);

        await host.ReconfigureAsync(settings);
        Assert.Equal(2, gate.KeywordPushes);
    }

    [Fact]
    public async Task The_two_engines_are_managed_independently()
    {
        await using var host = new LocalEngineHost();
        var ttsPort = ApiFixture.FreePort();
        var kwsPort = ApiFixture.FreePort();

        var both = await host.ReconfigureAsync(new LocalSettings
        {
            Tts = Engine(ttsPort),
            Kws = Engine(kwsPort),
        });
        Assert.True(both.Tts.Running);
        Assert.True(both.Kws.Running);
        var ttsPid = both.Tts.ProcessId!.Value;

        var kwsOnly = await host.ReconfigureAsync(new LocalSettings
        {
            Tts = Engine(ttsPort, enabled: false),
            Kws = Engine(kwsPort),
        });
        Assert.False(kwsOnly.Tts.Running);
        Assert.True(kwsOnly.Kws.Running);
        await AssertProcessGoneAsync(ttsPid);
    }

    [Fact]
    public async Task Missing_command_reports_a_reason_instead_of_throwing()
    {
        await using var host = new LocalEngineHost();

        var state = await host.ReconfigureAsync(new LocalSettings
        {
            Tts = new LocalEngineSettings { Enabled = true, Port = ApiFixture.FreePort() },
        });

        Assert.False(state.Tts.Running);
        Assert.Contains("未配置", state.Tts.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Port_placeholder_is_expanded_into_the_argument()
    {
        await using var gate = await HealthGate.StartAsync();
        gate.Ready = true;
        var output = Path.Combine(Path.GetTempPath(), $"partner-port-{Guid.NewGuid():N}.txt");
        try
        {
            await using var host = new LocalEngineHost();
            var escapedPath = output.Replace("'", "''", StringComparison.Ordinal);
            var state = await host.ReconfigureAsync(new LocalSettings
            {
                Tts = new LocalEngineSettings
                {
                    Enabled = true,
                    Command = "powershell",
                    Arguments = ["-NoProfile", "-Command", $"[System.IO.File]::WriteAllText('{escapedPath}', '{{port}}'); Start-Sleep -Seconds 60"],
                    Port = gate.Port,
                    ReadyTimeoutSeconds = 10,
                },
            });

            Assert.True(state.Tts.Running, state.Tts.Reason);
            var watch = Stopwatch.StartNew();
            while ((!File.Exists(output) || new FileInfo(output).Length == 0) && watch.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(50);
            Assert.True(File.Exists(output), "The engine did not write its received port within 20 seconds.");
            Assert.Equal(gate.Port.ToString(), await File.ReadAllTextAsync(output));
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public async Task Dispose_stops_every_running_engine()
    {
        var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();
        var started = await host.ReconfigureAsync(new LocalSettings
        {
            Tts = Engine(port),
            Kws = Engine(ApiFixture.FreePort()),
        });
        var ttsPid = started.Tts.ProcessId!.Value;

        await host.DisposeAsync();

        await AssertProcessGoneAsync(ttsPid);
    }

    [Fact]
    public async Task Listening_and_authenticated_views_precede_engine_readiness_and_halves_start_independently()
    {
        await using var h = await WarmupHarness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = h.Http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/health")).StatusCode);
        foreach (var path in new[] { "/api/health", "/api/settings", "/api/session" })
            Assert.Equal(HttpStatusCode.OK, (await h.Http.GetAsync(path)).StatusCode);

        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(
            h.Engines.State.Tts.ProcessId.HasValue && h.Engines.State.Kws.ProcessId.HasValue)));
        Assert.False(h.Engines.State.Tts.Running);
        Assert.False(h.Engines.State.Kws.Running);
        Assert.False(h.Voice.State.Input.Running);
        Assert.False(h.Voice.State.Output.Running);

        h.Tts.Ready = true;
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(h.Voice.State.Output.Running)));
        var sink = h.Components.LastSink;
        Assert.NotNull(sink);
        Assert.False(h.Voice.State.Input.Running);
        h.Kws.Ready = true;
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(h.Voice.State.Input.Running)));
        Assert.Same(sink, h.Components.LastSink);
    }

    [Fact]
    public async Task Input_is_not_reopened_when_output_becomes_ready()
    {
        await using var h = await WarmupHarness.StartAsync();
        h.Kws.Ready = true;
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(h.Voice.State.Input.Running)));
        var source = h.Components.LastSource;
        Assert.NotNull(source);
        h.Tts.Ready = true;
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(h.Voice.State.Output.Running)));
        Assert.Same(source, h.Components.LastSource);
        Assert.Equal(1, h.Components.SourcesOpened);
    }

    [Fact]
    public async Task Output_health_timeout_does_not_interrupt_ready_input()
    {
        await using var h = await WarmupHarness.StartAsync(readyTimeoutSeconds: 1);
        h.Kws.Ready = true;
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(h.Voice.State.Input.Running)));
        var source = h.Components.LastSource;
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(h.Engines.State.Tts.Reason.Contains("超时", StringComparison.Ordinal))));
        Assert.True(h.Voice.State.Input.Running);
        Assert.Same(source, h.Components.LastSource);
        Assert.Equal(1, h.Components.SourcesOpened);
    }

    [Fact]
    public async Task Changing_settings_during_warmup_cancels_old_process_and_shutdown_reaps_remaining_process()
    {
        await using var h = await WarmupHarness.StartAsync();
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(
            h.Engines.State.Tts.ProcessId.HasValue && h.Engines.State.Kws.ProcessId.HasValue)));
        var oldTts = h.Engines.State.Tts.ProcessId!.Value;
        var settings = h.Context.Settings.Load();
        using var response = await h.Http.PutSettingsAsync(settings with
        {
            Local = settings.Local with { Tts = settings.Local.Tts with { Enabled = false } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(
            h.Engines.State.Kws.ProcessId.HasValue && h.Engines.State.Kws.ProcessId != oldTts)));
        Assert.False(h.Engines.State.Tts.Running);
        await AssertProcessGoneAsync(oldTts);
        var kws = h.Engines.State.Kws.ProcessId!.Value;
        await h.App.StopAsync();
        await AssertProcessGoneAsync(kws);
    }

    private sealed class HealthGate : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int Port { get; }
        public volatile bool Ready;
        public volatile bool FailKeywordPush;
        public int KeywordPushes;

        private HealthGate(WebApplication app, int port) { _app = app; Port = port; }

        public static async Task<HealthGate> StartAsync()
        {
            var port = ApiFixture.FreePort();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");
            var app = builder.Build();
            var gate = new HealthGate(app, port);
            app.MapGet("/health", () => Results.StatusCode(gate.Ready ? 200 : 503));
            app.MapPut("/keywords", () =>
            {
                Interlocked.Increment(ref gate.KeywordPushes);
                return Results.StatusCode(gate.FailKeywordPush ? 503 : 200);
            });
            await app.StartAsync();
            return gate;
        }

        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    private sealed class WarmupHarness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required ApiContext Context { get; init; }
        public required ConsumerHost Consumer { get; init; }
        public required VoiceHost Voice { get; init; }
        public required LocalEngineHost Engines { get; init; }
        public required MemoryMaintenanceService Maintenance { get; init; }
        public required SyntheticVoice Components { get; init; }
        public required HealthGate Tts { get; init; }
        public required HealthGate Kws { get; init; }
        public required HttpClient Http { get; init; }
        public required string Workspace { get; init; }

        public static async Task<WarmupHarness> StartAsync(int readyTimeoutSeconds = 30)
        {
            var tts = await HealthGate.StartAsync();
            var kws = await HealthGate.StartAsync();
            var workspace = Path.Combine(Path.GetTempPath(), "partner-warmup", Guid.NewGuid().ToString("N"));
            var port = ApiFixture.FreePort();
            var token = TokenAuth.Generate();
            var components = new SyntheticVoice();
            var (app, context, consumer, voice, engines, maintenance) =
                await ApiHost.BuildAsync(workspace, port, token, components: components);
            var command = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            LocalEngineSettings ProcessFor(int enginePort) => new()
            {
                Enabled = true, Command = command, Port = enginePort, ReadyTimeoutSeconds = readyTimeoutSeconds,
                Arguments = ["-NoProfile", "-Command", "Start-Sleep -Seconds 60"],
            };
            var settings = context.Settings.Load();
            context.Settings.Save(settings with
            {
                Input = settings.Input with { Enabled = true },
                Output = settings.Output with { Enabled = true },
                Asr = settings.Asr with { BaseUrl = $"http://127.0.0.1:{kws.Port}" },
                Tts = settings.Tts with { BaseUrl = $"http://127.0.0.1:{tts.Port}" },
                Local = new LocalSettings { Tts = ProcessFor(tts.Port), Kws = ProcessFor(kws.Port) },
            });
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return new WarmupHarness
            {
                App = app, Context = context, Consumer = consumer, Voice = voice, Engines = engines,
                Maintenance = maintenance, Components = components, Tts = tts, Kws = kws, Http = http, Workspace = workspace,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            Maintenance.Stop();
            await Maintenance.DisposeAsync();
            await Engines.DisposeAsync();
            await Voice.DisposeAsync();
            await Consumer.DisposeAsync();
            await Context.DisposeAsync();
            await App.DisposeAsync();
            await Tts.DisposeAsync();
            await Kws.DisposeAsync();
            if (Directory.Exists(Workspace)) Directory.Delete(Workspace, recursive: true);
        }
    }

    /// <summary>A non-zero exit means the process is alive or the PID was reused; both fail the assertion.</summary>
    private static async Task AssertProcessGoneAsync(int pid)
    {
        // On Windows the process object may be disposed while the PID stays reusable until all handles close;
        // checking for a live process in a short window is enough.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // absent means exited.
            }
            catch (InvalidOperationException)
            {
                return; // HasExited cannot decide; most likely it just exited.
            }

            await Task.Delay(100);
        }

        Assert.Fail($"进程 {pid} 在停止后仍然存活。");
    }
}
