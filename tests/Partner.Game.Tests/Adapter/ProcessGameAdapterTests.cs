using System.Diagnostics;
using System.Globalization;
using Partner.Game.Adapter;

namespace Partner.Game.Tests.Adapter;

/// <summary>
/// End-to-end tests of <see cref="ProcessGameAdapter"/> against the real
/// <c>partner-game-testserver</c> child process: the adapter is only interesting across a process boundary.
/// </summary>
public sealed class ProcessGameAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "partner-game-adapter-tests", Guid.NewGuid().ToString("n"));

    public ProcessGameAdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup must not fail the test run.
        }
    }

    [Fact]
    public async Task Attach_detach_lifecycle_is_idempotent_and_guards_calls()
    {
        await using var adapter = new ProcessGameAdapter(Options());

        Assert.False(adapter.IsAttached);
        Assert.Equal("grid-world", adapter.GameId);

        await adapter.AttachAsync();
        Assert.True(adapter.IsAttached);

        await adapter.DetachAsync();
        Assert.False(adapter.IsAttached);

        await adapter.DetachAsync(); // idempotent: a second detach is a no-op.
        Assert.False(adapter.IsAttached);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ObserveAsync());
    }

    [Fact]
    public async Task Observe_returns_the_initial_state_of_a_fresh_process()
    {
        await using var adapter = new ProcessGameAdapter(Options());
        await adapter.AttachAsync();

        var snapshot = await adapter.ObserveAsync();

        Assert.Equal(0, snapshot.GetInt("x"));
        Assert.Equal(0, snapshot.GetInt("y"));
        Assert.Equal(20, snapshot.GetInt("hp"));
        Assert.Equal(1, snapshot.GetInt("enemies"));
        Assert.Equal(0, snapshot.GetInt("tick"));
        Assert.Equal(5, snapshot.Values.Count);
        Assert.True(snapshot.Seq > 0, $"expected a non-zero seq, got {snapshot.Seq}");
        Assert.True(snapshot.At > DateTimeOffset.UtcNow.AddMinutes(-1), $"stale timestamp {snapshot.At}");
    }

    [Fact]
    public async Task Act_mutates_the_world_and_clamps_to_the_grid()
    {
        await using var adapter = new ProcessGameAdapter(Options());
        await adapter.AttachAsync();

        var attacked = await adapter.ActAsync(GameActionRequest.Of("attack"));
        Assert.True(attacked.Ok);
        Assert.Equal("attacked", attacked.Detail);
        Assert.Equal(0, attacked.After!.GetInt("enemies"));
        Assert.Equal(1, attacked.After.GetInt("tick"));

        var ate = await adapter.ActAsync(GameActionRequest.Of("eat"));
        Assert.True(ate.Ok);
        Assert.Equal(20, ate.After!.GetInt("hp")); // already at full hp: capped, not 25.

        var moved = await adapter.ActAsync(GameActionRequest.Of("move", ("dx", "3"), ("dy", "4")));
        Assert.True(moved.Ok);
        Assert.Equal(3, moved.After!.GetInt("x"));
        Assert.Equal(4, moved.After.GetInt("y"));

        var clamped = await adapter.ActAsync(GameActionRequest.Of("move", ("dx", "100"), ("dy", "-50")));
        Assert.True(clamped.Ok);
        Assert.Equal(9, clamped.After!.GetInt("x"));
        Assert.Equal(0, clamped.After.GetInt("y"));
        Assert.Equal(4, clamped.After.GetInt("tick")); // every successful act ticks.
    }

    [Fact]
    public async Task Outcome_carries_the_post_action_state()
    {
        await using var adapter = new ProcessGameAdapter(Options());
        await adapter.AttachAsync();

        var outcome = await adapter.ActAsync(GameActionRequest.Of("move", ("dx", "2"), ("dy", "1")));

        Assert.NotNull(outcome.After);
        Assert.True(outcome.LatencyMs > 0);

        var observed = await adapter.ObserveAsync();
        Assert.Equal(
            observed.Values.OrderBy(pair => pair.Key),
            outcome.After!.Values.OrderBy(pair => pair.Key));
        Assert.True(observed.Seq > outcome.After.Seq, "the follow-up observe must be a later state");
    }

    [Fact]
    public async Task Latency_is_measured_over_observe_and_act()
    {
        await using var adapter = new ProcessGameAdapter(Options());
        await adapter.AttachAsync();

        for (var i = 0; i < 3; i++)
        {
            await adapter.ObserveAsync();
        }

        for (var i = 0; i < 2; i++)
        {
            await adapter.ActAsync(GameActionRequest.Of("attack"));
        }

        var stats = adapter.Latency;
        Assert.True(stats.Samples >= 5, $"expected >= 5 samples, got {stats.Samples}");
        Assert.True(stats.P50Ms > 0, $"expected a positive p50, got {stats.P50Ms}");
        Assert.True(stats.P99Ms >= stats.P50Ms, $"p99 {stats.P99Ms} < p50 {stats.P50Ms}");
        Assert.True(stats.MaxMs >= stats.P99Ms, $"max {stats.MaxMs} < p99 {stats.P99Ms}");
    }

    [Fact]
    public async Task Concurrent_calls_are_serialised_on_the_pipe()
    {
        await using var adapter = new ProcessGameAdapter(Options());
        await adapter.AttachAsync();

        var before = adapter.Latency.Samples;

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => adapter.ObserveAsync()));

        Assert.Equal(8, results.Length);
        Assert.All(results, snapshot => Assert.Equal(20, snapshot.GetInt("hp")));

        // One response per request: no interleaved lines, no lost or duplicated responses.
        Assert.Equal(8, results.Select(snapshot => snapshot.Seq).Distinct().Count());
        Assert.Equal(before + 8, adapter.Latency.Samples);
    }

    [Fact]
    public async Task Call_timeout_throws_naming_the_command_and_leaves_the_adapter_attached()
    {
        await using var adapter = new ProcessGameAdapter(
            Options(TimeSpan.FromMilliseconds(300), "--hang-on", "observe"));
        await adapter.AttachAsync();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => adapter.ObserveAsync());

        Assert.Contains("observe", error.Message);
        Assert.Contains("300", error.Message);
        Assert.True(adapter.IsAttached);
    }

    [Fact]
    public async Task Unknown_action_is_reported_not_thrown()
    {
        await using var adapter = new ProcessGameAdapter(Options());
        await adapter.AttachAsync();

        var outcome = await adapter.ActAsync(GameActionRequest.Of("nope"));

        Assert.False(outcome.Ok);
        Assert.Contains("nope", outcome.Detail);
        Assert.Null(outcome.After);
    }

    [Fact]
    public async Task Killed_child_fails_the_next_call_and_reports_detached()
    {
        var pidFile = Path.Combine(_dir, "game.pid");
        await using var adapter = new ProcessGameAdapter(
            Options(TimeSpan.FromSeconds(5), "--pid-file", pidFile));
        await adapter.AttachAsync();

        var pid = await ReadPidAsync(pidFile);
        using (var child = Process.GetProcessById(pid))
        {
            child.Kill();
            await child.WaitForExitAsync();
        }

        var call = Assert.ThrowsAnyAsync<Exception>(() => adapter.ObserveAsync());
        var error = await call.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(
            error is InvalidOperationException or TimeoutException,
            $"expected InvalidOperationException or TimeoutException, got {error.GetType().Name}: {error.Message}");
        Assert.False(adapter.IsAttached);
    }

    [Fact]
    public async Task Observe_before_attach_throws()
    {
        await using var adapter = new ProcessGameAdapter(Options());

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ObserveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ActAsync(GameActionRequest.Of("attack")));
    }

    private static ProcessGameAdapterOptions Options(TimeSpan? callTimeout = null, params string[] extraArguments)
    {
        var arguments = new List<string> { TestServerDll() };
        arguments.AddRange(extraArguments);

        return new ProcessGameAdapterOptions
        {
            GameId = "grid-world",
            Command = "dotnet",
            Arguments = arguments,
            CallTimeout = callTimeout ?? TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Walks up from the test output directory to the repository root and takes the newest
    /// build of the game test server. Never skips silently.
    /// </summary>
    private static string TestServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not find 'Partner.sln' above '{AppContext.BaseDirectory}'; " +
                "cannot locate the game test server.");
        }

        // Pick the newest build; the framework folder name changes with the TFM.
        var binRoot = Path.Combine(directory.FullName, "tests", "Partner.GameTestServer", "bin");
        var candidates = Directory.Exists(binRoot)
            ? Directory.GetFiles(binRoot, "partner-game-testserver.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray()
            : Array.Empty<string>();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"The game test server is not built; expected 'partner-game-testserver.dll' under " +
                $"'{binRoot}\\<configuration>\\<framework>'. Run `dotnet build` on the solution before running these tests.");
        }

        return candidates[0];
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (true)
        {
            try
            {
                if (File.Exists(pidFile)
                    && int.TryParse(
                        await File.ReadAllTextAsync(pidFile),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var pid)
                    && pid > 0)
                {
                    return pid;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The child may still be holding the file open; retry below.
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new InvalidOperationException(
                    $"The game test server did not write its PID to '{pidFile}' within 15s.");
            }

            await Task.Delay(25);
        }
    }
}
