namespace Partner.Core.Agent.Web;

/// <summary>One engine that may be asked, and the reason it was put in the pool.</summary>
/// <param name="Engine">The engine.</param>
/// <param name="Role">Why it is here — a record lookup, a kind specialist, breadth, or a substitute.</param>
/// <param name="Why">The reason in words, reused by the answer's "who answered" block.</param>
public sealed record PlannedEngine(IWebSearchEngine Engine, EnginePoolRole Role, string Why);

/// <summary>
/// The plan: who gets asked, and who does not with the reason. The second list is not diagnostics — it is part
/// of the answer, because "nothing found" and "I did not ask anyone who could find it" are different facts.
/// </summary>
public sealed record EnginePool(IReadOnlyList<PlannedEngine> Engines, IReadOnlyList<string> Skipped);

/// <summary>
/// What the planner knows about the runtime that the engines cannot: what the user switched off, and what is
/// currently in cooldown. Passed as data rather than as the health store, so the planner stays pure and testable.
/// </summary>
/// <param name="Disabled">Engine ids the user switched off in settings.</param>
/// <param name="Unavailable">Why an engine may not be asked right now (a cooldown), or null.</param>
/// <param name="Rotation">
/// A counter advanced once per search. It shifts which engines survive the pool cap, so a permanently-long
/// candidate list does not permanently starve its tail.
/// </param>
public sealed record WebPlanContext(
    IReadOnlySet<string> Disabled,
    Func<string, string?> Unavailable,
    int Rotation = 0)
{
    /// <summary>Everything allowed, nothing cooling down. For tests and for the first search of a session.</summary>
    public static WebPlanContext Open { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        _ => null);
}

/// <summary>
/// The first quality gate: asking the right people. Pure and offline — it reads what each engine declared about
/// itself and decides the pool, with no hand-written routing table to fall out of step with the engines.
/// </summary>
public sealed class QueryPlanner
{
    /// <summary>
    /// How many engines one search may ask. Above this the fanout's latency budget is spent on sources that
    /// mostly repeat each other; below it a single blocked engine costs the answer its breadth.
    /// </summary>
    public const int MaxPool = 6;

    /// <summary>Below this many usable breadth engines the reserves are allowed in.</summary>
    public const int MinBaseForNoRescue = 2;

    private readonly IReadOnlyList<IWebSearchEngine> _engines;

    public QueryPlanner(IReadOnlyList<IWebSearchEngine>? engines = null) => _engines = engines ?? WebEngines.All;

    public EnginePool Plan(SearchQuery query, WebPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var script = QueryScript.Detect(query.Text);
        var kindMask = MaskOf(query.Kind);
        var wantsNews = query.Kind == SearchKinds.News || query.Recency is not null;

        var records = new List<PlannedEngine>();
        var specialists = new List<PlannedEngine>();
        var bases = new List<PlannedEngine>();
        var reserves = new List<PlannedEngine>();
        var skipped = new List<string>();

        foreach (var engine in _engines)
        {
            var traits = engine.Traits;

            if (traits.NeedsCredentials)
            {
                // Never routed to automatically, whatever else it declares. A capability that quietly depends on
                // somebody's account is not a capability that can be promised.
                continue;
            }

            if (context.Disabled.Contains(traits.Id))
            {
                skipped.Add($"{traits.Name}：你已在设置里停用");
                continue;
            }

            if (context.Unavailable(traits.Id) is { } reason)
            {
                skipped.Add($"{traits.Name}：{reason}");
                continue;
            }

            if (traits.Kinds == ContentKinds.None)
            {
                continue;
            }

            // Shape routing comes first and is exempt from the script filter: an engine that matched the question
            // declared, by doing so, that it can answer this query.
            if (traits.Handles?.Invoke(query.Text) == true)
            {
                records.Add(new PlannedEngine(engine, EnginePoolRole.Specialist, "问题形态直答"));
                continue;
            }

            var covers = traits.Scripts == QueryScripts.Any || (traits.Scripts & script) != 0;
            if (!covers)
            {
                if (traits.Role == EnginePoolRole.Specialist && (kindMask & traits.Kinds) != 0)
                {
                    // Worth saying out loud: the caller asked for this kind and the only engine for it cannot
                    // read the language the question was written in.
                    skipped.Add($"{traits.Name}：只检索拉丁文查询，本次查询不是");
                }

                continue;
            }

            if (traits.Role == EnginePoolRole.Specialist && (kindMask & traits.Kinds) != 0)
            {
                specialists.Add(new PlannedEngine(engine, EnginePoolRole.Specialist, $"kind={query.Kind}"));
                continue;
            }

            if (traits.Role == EnginePoolRole.Specialist && wantsNews && (traits.Kinds & ContentKinds.News) != 0)
            {
                specialists.Add(new PlannedEngine(engine, EnginePoolRole.Specialist, "时效诉求"));
                continue;
            }

            if (traits.Role == EnginePoolRole.Base)
            {
                bases.Add(new PlannedEngine(engine, EnginePoolRole.Base, "广度"));
                continue;
            }

            if (traits.Role == EnginePoolRole.Reserve)
            {
                reserves.Add(new PlannedEngine(engine, EnginePoolRole.Reserve, "替补"));
            }
        }

        // The reserve is a bounded, deliberate substitute for breadth that is missing, not a queue: a beat where
        // two breadth engines answered does not need a third one's latency.
        if (bases.Count < MinBaseForNoRescue)
        {
            bases.AddRange(reserves);
        }
        else if (reserves.Count > 0)
        {
            skipped.Add($"替补源（{string.Join("、", reserves.Select(r => r.Engine.Traits.Name))}）：基础源够用，本次不用");
        }

        var ordered = new List<PlannedEngine>();
        ordered.AddRange(Rotate(records, context.Rotation));
        ordered.AddRange(Rotate(specialists, context.Rotation));
        ordered.AddRange(Rotate(bases, context.Rotation));

        if (ordered.Count > MaxPool)
        {
            var dropped = ordered.Skip(MaxPool).ToList();
            ordered = ordered.Take(MaxPool).ToList();
            skipped.Add($"名额上限 {MaxPool}：本轮未问 {string.Join("、", dropped.Select(d => d.Engine.Traits.Name))}");
        }

        return new EnginePool(ordered, skipped);
    }

    /// <summary>Which content kinds a <c>kind</c> value selects.</summary>
    public static ContentKinds MaskOf(string? kind) => SearchKinds.Normalize(kind) switch
    {
        SearchKinds.News => ContentKinds.News,
        SearchKinds.Academic => ContentKinds.Academic,
        SearchKinds.Code => ContentKinds.Code,
        SearchKinds.Encyclopedia => ContentKinds.Encyclopedia,
        _ => ContentKinds.General,
    };

    /// <summary>
    /// Shifts a group so the engines at the cap's edge rotate between searches. Without it, the last registered
    /// engine of a long group would never be asked, and nobody would find out why.
    /// </summary>
    private static IEnumerable<PlannedEngine> Rotate(List<PlannedEngine> group, int rotation)
    {
        if (group.Count <= 1)
        {
            return group;
        }

        var offset = ((rotation % group.Count) + group.Count) % group.Count;
        return group.Skip(offset).Concat(group.Take(offset));
    }
}
