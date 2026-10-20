using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Web;

/// <summary>
/// One search, end to end: plan the pool, fan out, gate, merge, filter, say what happened.
/// <para>
/// This is the only class that knows the order of the gates. Each stage is separate and testable on its own, and
/// each has a reason to exist that the others do not cover — the planner asks the right sources, the gate
/// catches failures dressed as results, the merger makes one list from several, and the formatter keeps the
/// answer honest about all of it.
/// </para>
/// </summary>
public sealed class WebSearchService : IDisposable
{
    private readonly Func<WebOptions> _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly WebTransport _transport;
    private readonly QueryPlanner _planner;
    private readonly BatchGate _gate = new();
    private readonly Merger _merger = new();
    private readonly ResultFormatter _formatter = new();
    private readonly SearchCache _cache;
    private readonly EngineHealth _health;
    private readonly WebFanout _fanout;

    private int _rotation;

    /// <summary>
    /// Which hosts this session has seen. Filled by searches and by <see cref="WebHostLedgerHook"/>, read by the
    /// governance layer to decide whether opening a URL is a lead being followed or a destination being chosen.
    /// </summary>
    public WebHostLedger Ledger { get; } = new();

    /// <param name="options">Read live on every search, so a settings edit applies to the next question.</param>
    /// <param name="workspace">Where the health state lives.</param>
    /// <param name="clock">Injected for tests.</param>
    /// <param name="engines">The engine set. Defaults to every engine this build has.</param>
    public WebSearchService(
        Func<WebOptions> options,
        string workspace,
        Func<DateTimeOffset>? clock = null,
        IReadOnlyList<IWebSearchEngine>? engines = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        _options = options;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _planner = new QueryPlanner(engines);

        // The transport reads the proxy through the same delegate, so a proxy edit rebuilds the client lazily
        // rather than needing the consumer to restart.
        _transport = new WebTransport(() => _options().Proxy);
        _health = EngineHealth.Load(workspace, _clock);
        _cache = new SearchCache(_clock);
        _fanout = new WebFanout(_transport, new RateLimiter(), _clock);
    }

    /// <summary>The time budget one search is allowed, read live from the settings.</summary>
    public TimeSpan Budget => _options().Budget;

    /// <summary>Exposed for the settings page, which shows every engine's state and last failure.</summary>
    public EngineHealth Health => _health;

    /// <summary>The shared transport, for the page reader, so both traffic paths share one identity.</summary>
    public WebTransport Transport => _transport;

    /// <summary>
    /// Runs one search and returns the text the model will read.
    /// <para>
    /// Failures come back as <see cref="ToolResult.Ok"/> = false with a prefixed line, so the job engine's
    /// retry classification treats them the way it treats every other tool's.
    /// </para>
    /// </summary>
    public async Task<ToolResult> SearchAsync(
        string? text,
        string? kind,
        string? recency,
        int? limit,
        CancellationToken ct = default)
    {
        var options = _options();
        if (!options.Enabled)
        {
            return new ToolResult(false, "denied: 联网搜索未启用（设置 → 联网搜索）");
        }

        var query = text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return new ToolResult(false, "invalid_args: 需要 query");
        }

        if (recency is not null && !SearchRecency.IsKnown(recency))
        {
            return new ToolResult(false, $"invalid_args: recency 只能是 {string.Join('|', SearchRecency.Values)}");
        }

        var request = new SearchQuery(
            Text: query,
            Kind: SearchKinds.Normalize(kind),
            Recency: recency,
            Limit: Math.Clamp(limit ?? options.MaxResults, WebOptions.MinResults, WebOptions.MaxResultsCeiling));

        if (_cache.TryGet(request.CacheKey, out var cached, out var age) && cached is not null)
        {
            foreach (var hit in cached.Hits)
            {
                Ledger.Remember(hit.Url);
            }

            return new ToolResult(true, _formatter.Format(cached, options.MaxResults, age));
        }

        var now = _clock();
        var pool = _planner.Plan(request, new WebPlanContext(
            options.Disabled,
            _health.Unavailable,
            Interlocked.Increment(ref _rotation)));

        FanoutOutcome fanout;
        if (pool.Engines.Count == 0)
        {
            fanout = new FanoutOutcome([], TimeSpan.Zero, null, false);
        }
        else
        {
            fanout = await _fanout.RunAsync(pool.Engines, request, options.Budget, ct).ConfigureAwait(false);
        }

        var gate = _gate.Apply(fanout.Attempts, request);
        var merged = _merger.Merge(gate.Hits);
        var kept = ApplyRecency(merged, request, now);

        var outcome = new SearchOutcome(
            Query: request,
            Hits: kept,
            Attempts: fanout.Attempts,
            Batches: gate.Batches,
            Skipped: pool.Skipped,
            Elapsed: fanout.Elapsed,
            At: now,
            BudgetExpired: fanout.BudgetExpired);

        RecordHealth(fanout.Attempts);

        // The results are what the ledger is for: the model may now open any of these without a banner, since
        // they came out of a search the person's own question started. A cached answer takes the same path, which
        // is what keeps a repeated question from asking for approval the second time.
        foreach (var hit in kept)
        {
            Ledger.Remember(hit.Url);
        }

        if (SearchCache.WorthCaching(outcome))
        {
            _cache.Set(outcome);
        }

        return new ToolResult(true, _formatter.Format(outcome, options.MaxResults));
    }

    /// <summary>
    /// Drops results that can be <i>proven</i> stale. Only a structured field or the engine's own relative
    /// phrase counts as proof: a date scraped out of prose may be the founding year of a company, and a result
    /// with no date at all is unknown rather than old. Both are kept and labelled instead.
    /// </summary>
    private static IReadOnlyList<SearchHit> ApplyRecency(
        IReadOnlyList<SearchHit> hits,
        SearchQuery query,
        DateTimeOffset now)
    {
        if (SearchRecency.Cutoff(query.Recency, now) is not { } cutoff)
        {
            return hits;
        }

        return hits
            .Where(hit => hit.Published is null
                || hit.DateSource is DateSource.None or DateSource.Text
                || hit.Published >= cutoff)
            .ToList();
    }

    private void RecordHealth(IReadOnlyList<EngineAttempt> attempts)
    {
        var answered = attempts.Count(attempt => attempt.Answered);

        foreach (var attempt in attempts)
        {
            // The control the silent-empty rule asks for is another engine in the same batch that did find
            // something — the engine's own result cannot vouch for itself.
            var companion = (answered - (attempt.Answered ? 1 : 0)) > 0;
            _health.Record(attempt, companion);
        }
    }

    public void Dispose() => _transport.Dispose();
}
