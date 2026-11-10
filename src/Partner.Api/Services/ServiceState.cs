namespace Partner.Api.Services;

/// <summary>Live state of one service instance, as returned by the management endpoints.</summary>
public sealed record ServiceState(
    bool Running,
    string? Error,
    int Port,
    long Requests,
    DateTimeOffset? LastActivityAt,
    int PendingRounds,
    DateTimeOffset? LastFlushAt,
    string? LastMemoryId);

/// <summary>Request count, last activity, and the most recent runtime error, updated from several request threads.</summary>
internal sealed class ServiceCounters
{
    private long _requests;
    private long _lastActivityTicks;
    private string? _error;

    public long Requests => Interlocked.Read(ref _requests);

    public DateTimeOffset? LastActivityAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastActivityTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// The last per-request failure (a protocol mismatch, an upstream error). Distinct from the bind error the
    /// instance records at start-up: this one arrives while the listener is healthy, and the next request that
    /// reaches the upstream clears it, so a fixed configuration stops reporting a stale error.
    /// </summary>
    public string? Error => Volatile.Read(ref _error);

    public void Note(DateTimeOffset now)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);
    }

    public void NoteError(string message) => Volatile.Write(ref _error, message);

    public void ClearError() => Volatile.Write(ref _error, null);
}