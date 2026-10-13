using Partner.Core.Memory;

namespace Partner.Api;

/// <summary>
/// Periodic memory self-maintenance: promotion, idle demotion, TTL trim, and the physical removal of
/// soft-deleted rows, in that order — the order they are numbered by in the design's consolidation pass.
/// <para>
/// It exists because the store otherwise only maintains itself as a side effect of reads and writes. A
/// notebook nobody opens for a month would never age at all: TTL-expired working records would sit there
/// forever, and an unconsolidated note would wait precisely until the next turn happens to run before it
/// becomes trimmable. Folding the pass into the recall path is what a bug report would call "slow every
/// turn after lunch"; folding it into a timer keeps the ledger in one place.
/// </para>
/// <para>
/// Started and stopped with <see cref="ConsumerHost.ReconfigureAsync"/>, like everything else that runs
/// between turns — a one-shot pass before the loop starts is not enough, because a workspace can sit idle
/// far longer than a TTL. Errors are logged to the console, never thrown: a failed pass must not take down
/// the consumer the person is talking to, and the next interval will try again.
/// </para>
/// </summary>
public sealed class MemoryMaintenanceService : IAsyncDisposable
{
    private readonly IMemoryStore _memory;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;

    /// <summary>Interval between passes. Default is one day; tests pass a shorter one.</summary>
    public MemoryMaintenanceService(IMemoryStore memory, TimeProvider? clock = null, TimeSpan? interval = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _clock = clock ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromDays(1);

        // No periodic work starts here: the host decides when the runtime is up, and the first pass waits
        // one full interval so a restart does not immediately repeat the pass the previous process just ran.
        _timer = new Timer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts the periodic pass.</summary>
    public void Start() => _timer.Change(_interval, _interval);

    /// <summary>Stops the periodic pass and drops nothing in flight.</summary>
    public void Stop() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>One pass. Counted so a test can score it.</summary>
    public async Task<MemoryMaintenance.Run> RunAsync(CancellationToken ct = default)
    {
        var promoted = await _memory.PromoteEligibleAsync(ct).ConfigureAwait(false);
        var demoted = await _memory.DemoteIdleAsync(ct).ConfigureAwait(false);
        var trimmed = await _memory.TrimAsync(ct).ConfigureAwait(false);
        var compacted = await _memory.CompactAsync(ct).ConfigureAwait(false);

        return new MemoryMaintenance.Run(promoted, demoted, trimmed, compacted);
    }

    private void Tick(object? state)
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"memory maintenance pass failed: {error.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What one maintenance pass did, on each of its counters.</summary>
public static class MemoryMaintenance
{
    public sealed record Run(int Promoted, int Demoted, int Trimmed, int Compacted);
}
