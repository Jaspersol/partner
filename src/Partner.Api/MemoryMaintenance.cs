using Partner.Core.Memory;

namespace Partner.Api;

/// <summary>
/// Periodic memory maintenance: promotion, demotion, TTL trim, and physical removal of soft-deleted rows.
/// Started by <see cref="ApiHost"/>; errors are logged so a failed pass does not take down the consumer.
/// </summary>
public sealed class MemoryMaintenanceService : IAsyncDisposable
{
    private readonly IMemoryStore _memory;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private Task? _catchUp;

    /// <summary>Interval between passes. Default is one day; tests pass a shorter one.</summary>
    public MemoryMaintenanceService(IMemoryStore memory, TimeProvider? clock = null, TimeSpan? interval = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _clock = clock ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromDays(1);

        // No periodic work starts here: the host decides when the runtime is up, and the first pass waits
        // one full interval so a restart does not immediately repeat the previous process's pass.
        _timer = new Timer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts the timer and takes a startup backup if one is due.</summary>
    public void Start()
    {
        _timer.Change(_interval, _interval);
        _catchUp ??= Task.Run(EnsureBackupAsync);
    }

    /// <summary>Stops the periodic pass and drops nothing in flight.</summary>
    public void Stop() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>One pass. Counted so a test can score it.</summary>
    public async Task<MemoryMaintenance.Run> RunAsync(CancellationToken ct = default)
    {
        // Back up before this pass permanently removes soft-deleted rows.
        await _memory.EnsureBackupAsync(_interval, keep: 7, ct).ConfigureAwait(false);

        var promoted = await _memory.PromoteEligibleAsync(ct).ConfigureAwait(false);
        var demoted = await _memory.DemoteIdleAsync(ct).ConfigureAwait(false);
        var trimmed = await _memory.TrimAsync(ct).ConfigureAwait(false);
        var compacted = await _memory.CompactAsync(ct).ConfigureAwait(false);

        return new MemoryMaintenance.Run(promoted, demoted, trimmed, compacted);
    }

    private async Task EnsureBackupAsync()
    {
        try
        {
            await _memory.EnsureBackupAsync(_interval, keep: 7).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"memory backup catch-up failed: {error.Message}");
        }
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

        // Let an in-flight startup backup finish before the store closes.
        if (_catchUp is not null)
        {
            await _catchUp.ConfigureAwait(false);
        }
    }
}

/// <summary>What one maintenance pass did, on each of its counters.</summary>
public static class MemoryMaintenance
{
    public sealed record Run(int Promoted, int Demoted, int Trimmed, int Compacted);
}
