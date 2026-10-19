namespace Partner.Core.Agent.Web;

/// <summary>
/// One request at a time to any one host, with a short gap between them.
/// <para>
/// The rate limiter is per engine, and an engine is not a host: two searches in a row are two requests to the same
/// host with nothing between them. A burst of those is the shape of a scraper, and it is exactly what a rate limit
/// or a captcha is there to stop. Serialising by host costs one round trip of latency at worst, which is the
/// cheapest thing this code can do to arrive politely.
/// </para>
/// <para>
/// The gap carries jitter on purpose. Requests sent exactly <see cref="DefaultGap"/> apart are as regular as a
/// metronome, and regularity is itself a signal; a varying gap is not.
/// </para>
/// </summary>
public sealed class HostGate
{
    /// <summary>The quiet period after one request to a host before the next one may start.</summary>
    public static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(250);

    /// <summary>How far the gap may move either way, as a fraction of itself.</summary>
    private const double JitterFraction = 0.2;

    /// <summary>Hosts tracked before finished entries are dropped. A session does not talk to many more.</summary>
    private const int MaxHosts = 128;

    private readonly Dictionary<string, Task> _tails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _startedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeSpan _gap;
    private readonly Func<double> _random;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="gap">The quiet period. Defaults to <see cref="DefaultGap"/>.</param>
    /// <param name="random">Source of the jitter, in [0,1). Injected so a test can pin the gap.</param>
    /// <param name="clock">Injected for the same reason.</param>
    /// <param name="delay">The wait itself, injected so a test does not have to spend it.</param>
    public HostGate(
        TimeSpan? gap = null,
        Func<double>? random = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gap = gap ?? DefaultGap;
        _random = random ?? (() => Random.Shared.NextDouble());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    /// <summary>
    /// Waits for this host's turn. Returns at once when the host has not been asked recently, which is the normal
    /// case for the first request of a session and for every host the fanout only touches once.
    /// </summary>
    public Task WaitAsync(string host, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        Task previous;
        Task mine;

        lock (_lock)
        {
            Prune();
            previous = _tails.TryGetValue(host, out var tail) ? tail : Task.CompletedTask;
            mine = AfterAsync(host, previous, ct);
            _tails[host] = mine;
        }

        return mine;
    }

    private async Task AfterAsync(string host, Task previous, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The request ahead of this one failing is not this one's problem. Its turn is over either way.
        }

        DateTimeOffset started;
        bool seen;
        lock (_lock)
        {
            seen = _startedAt.TryGetValue(host, out started);
        }

        if (seen)
        {
            var wait = Gap() - (_clock() - started);
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, ct).ConfigureAwait(false);
            }
        }

        lock (_lock)
        {
            _startedAt[host] = _clock();
        }
    }

    /// <summary>The quiet period, moved by up to <see cref="JitterFraction"/> in either direction.</summary>
    private TimeSpan Gap()
    {
        var spread = (_random() * 2 - 1) * JitterFraction;
        return _gap * (1 + spread);
    }

    /// <summary>Drops the finished hosts once the table has grown past its ceiling. Called under the lock.</summary>
    private void Prune()
    {
        if (_tails.Count <= MaxHosts)
        {
            return;
        }

        foreach (var host in _tails.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
        {
            _tails.Remove(host);
            _startedAt.Remove(host);
        }
    }
}
