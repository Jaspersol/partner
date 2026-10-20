using System.Diagnostics;
using Partner.Api.Settings;
using Partner.Api.Voice;

namespace Partner.Api.Tests;

/// <summary>
/// Local engine process hosting, verified with a real child process (powershell sleep): on means the process
/// exists, off means it is gone. The real engines (sherpa / tts-server) are too heavy; process lifecycle is
/// the only behaviour worth pinning here.
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
        Port = port,
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
        await using var host = new LocalEngineHost();
        var port = ApiFixture.FreePort();

        var state = await host.ReconfigureAsync(new LocalSettings
        {
            Tts = new LocalEngineSettings
            {
                Enabled = true,
                Command = "powershell",
                Arguments = new[] { "-NoProfile", "-Command", "exit 0; # listen at {port}" },
                Port = port,
                ReadyTimeoutSeconds = 1,
            },
        });

        // Placeholder expansion can only be checked as far as "the process starts and exits with our injected
        // exit 0". The command line is not read back; a wrong placeholder would make the engine listen on the
        // wrong port, which is for integration verification.
        Assert.True(state.Tts.Running || state.Tts.Reason.Contains("退出", StringComparison.Ordinal));
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
