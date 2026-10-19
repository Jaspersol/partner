using System.Diagnostics;

namespace Partner.Core.Agent.Web;

/// <summary>One engine's turn: what came back, or why nothing did, and how long it took.</summary>
public sealed record EngineAttempt(
    IWebSearchEngine Engine,
    string Why,
    ParseResult? Result,
    string? Failure,
    TimeSpan Elapsed)
{
    public IReadOnlyList<SearchHit> Hits => Result?.Hits ?? [];

    /// <summary>Whether this engine actually brought back results. The distinction the whole gate hangs on.</summary>
    public bool Answered => Result?.Status == ParseStatus.Ok && Hits.Count > 0;

    /// <summary>
    /// Why this engine has nothing to show, in the vocabulary the answer uses. Every path out of the fanout has
    /// one, so an engine can never be silently absent from the "who answered" block.
    /// </summary>
    public string Reason => Failure ?? Result switch
    {
        null => "unknown：没有可用的响应",
        { Status: ParseStatus.Blocked } => $"blocked：{Result.Note ?? "被拦截"}",
        { Status: ParseStatus.Unparseable } => $"unparseable：{Result.Note ?? "页面结构变了"}",
        { Status: ParseStatus.Empty } => $"empty：{Result.Note ?? "没有结果"}",
        _ => "empty：解析到了零条",
    };
}

/// <summary>What one fanout produced, including the parts that did not go well.</summary>
/// <param name="Attempts">Every engine that was asked, answered or not.</param>
/// <param name="Elapsed">Wall time for the whole fanout.</param>
/// <param name="TimeToFirstResult">When the first usable answer arrived, or null if none did.</param>
/// <param name="BudgetExpired">Whether the budget (not the hard ceiling) is what ended the wait.</param>
public sealed record FanoutOutcome(
    IReadOnlyList<EngineAttempt> Attempts,
    TimeSpan Elapsed,
    TimeSpan? TimeToFirstResult,
    bool BudgetExpired)
{
    /// <summary>The engines that are allowed to influence the health state as "a companion did find something".</summary>
    public int AnsweredCount => Attempts.Count(attempt => attempt.Answered);
}

/// <summary>
/// Runs a pool concurrently, under a latency policy built for a voice assistant: the clock does not start until
/// somebody has something to say.
/// <para>
/// The rule that matters is <c>budgetAfterFirstResult</c>. A fixed deadline throws away an answer that was two
/// hundred milliseconds late; no deadline keeps a person waiting on the slowest source in the pool. Waiting until
/// the first result arrives and only then starting the clock gets the honest version of both — and the hard
/// ceiling keeps a pool in which nobody ever answers from running forever.
/// </para>
/// </summary>
public sealed class WebFanout
{
    /// <summary>The outermost bound, even with no result at all. Past this, an empty answer beats no answer.</summary>
    public static readonly TimeSpan HardCeiling = TimeSpan.FromSeconds(30);

    /// <summary>How long a cancelled engine is allowed to wrap up, so its partial work is not thrown away.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(1);

    private readonly WebTransport _transport;
    private readonly RateLimiter _limiter;
    private readonly Func<DateTimeOffset> _clock;

    public WebFanout(WebTransport transport, RateLimiter? limiter = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _limiter = limiter ?? new RateLimiter();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="pool">Who to ask, in the planner's order.</param>
    /// <param name="query">The query every engine is asked.</param>
    /// <param name="budget">How long to keep waiting once the first result is in hand.</param>
    /// <param name="ct">Cancellation from the caller (the tool's own deadline, or the session ending).</param>
    public async Task<FanoutOutcome> RunAsync(
        IReadOnlyList<PlannedEngine> pool,
        SearchQuery query,
        TimeSpan budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(query);

        var stopwatch = Stopwatch.StartNew();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var running = pool
            .Select(planned => RunOneAsync(planned, query, cancellation.Token))
            .ToList();

        var completed = new List<EngineAttempt>();
        var hardCeiling = Task.Delay(HardCeiling, CancellationToken.None);
        Task? budgetExpiry = null;
        TimeSpan? firstResultAt = null;
        var budgetExpired = false;

        while (running.Count > 0)
        {
            var waiters = new List<Task> { hardCeiling };
            if (budgetExpiry is not null)
            {
                waiters.Add(budgetExpiry);
            }

            waiters.AddRange(running);

            var finished = await Task.WhenAny(waiters).ConfigureAwait(false);

            if (finished == hardCeiling)
            {
                break;
            }

            if (budgetExpiry is not null && finished == budgetExpiry)
            {
                budgetExpired = true;
                break;
            }

            running.Remove((Task<EngineAttempt>)finished);
            var attempt = await ((Task<EngineAttempt>)finished).ConfigureAwait(false);
            completed.Add(attempt);

            if (firstResultAt is null && attempt.Answered)
            {
                firstResultAt = stopwatch.Elapsed;
                budgetExpiry = Task.Delay(budget, CancellationToken.None);
            }
        }

        if (running.Count > 0)
        {
            cancellation.Cancel();

            var stragglers = Task.WhenAll(running);
            await Task.WhenAny(stragglers, Task.Delay(GracePeriod, CancellationToken.None)).ConfigureAwait(false);

            foreach (var task in running)
            {
                if (task.IsCompletedSuccessfully)
                {
                    // Finished inside the grace period, so its work is real and is reported as such.
                    completed.Add(task.Result);
                }
            }
        }

        stopwatch.Stop();
        return new FanoutOutcome(completed, stopwatch.Elapsed, firstResultAt, budgetExpired);
    }

    private async Task<EngineAttempt> RunOneAsync(PlannedEngine planned, SearchQuery query, CancellationToken ct)
    {
        var engine = planned.Engine;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var limit = engine.Traits.Rate;
            var wait = _limiter.Take(engine.Traits.Id, limit, _clock());

            if (wait > TimeSpan.Zero)
            {
                // A throttled engine is skipped, never queued behind. The fanout is parallel: one engine's bucket
                // delay would otherwise become everyone's, and the user is waiting for a sentence, not a search.
                if (limit.QueueFor <= TimeSpan.Zero || wait > limit.QueueFor)
                {
                    return new EngineAttempt(engine, planned.Why, null, "rate_limited：限流未拿到令牌，本次跳过", stopwatch.Elapsed);
                }

                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            using var request = engine.Build(query);
            var response = await _transport.SendAsync(request, ct).ConfigureAwait(false);

            if (response.Error is { } error)
            {
                return new EngineAttempt(engine, planned.Why, null, error, stopwatch.Elapsed);
            }

            var result = await engine
                .ParseAsync(response, query, new FollowUp(_transport), ct)
                .ConfigureAwait(false);

            return new EngineAttempt(engine, planned.Why, result, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new EngineAttempt(engine, planned.Why, null, "timeout：预算到点被取消", stopwatch.Elapsed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // An engine that throws is a bug in that engine, and it must cost that engine's turn rather than the
            // whole search. Reported with a prefix the job engine already classifies as retryable.
            return new EngineAttempt(
                engine,
                planned.Why,
                null,
                $"unreachable：解析 {engine.Traits.Id} 时出错（{error.Message}）",
                stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// The second request, for the one engine that needs it. Goes through the same transport, so it inherits the
    /// cookies, the headers and — the part that matters — the address check.
    /// </summary>
    private sealed class FollowUp(WebTransport transport) : IEngineFollowUp
    {
        public async Task<(string? Body, string? Error)> GetAsync(Uri url, CancellationToken ct = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            WebTransport.ApplyHeaders(request, WebTransport.AcceptLanguageFor(url), WebRequestKind.Data);

            var response = await transport.SendAsync(request, ct).ConfigureAwait(false);
            return response.Ok ? (response.Body, null) : (null, response.Error);
        }
    }
}
