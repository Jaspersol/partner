namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Hook registry: registration, per-phase ordering, and per-phase execution.
/// </summary>
public interface IHookRegistry
{
    /// <summary>
    /// Registers a hook. Throws when <see cref="IAgentHook.Name"/> is duplicated in the same phase or the
    /// constraints form a cycle, and the registration does not take effect.
    /// </summary>
    void Register(IAgentHook hook);

    /// <summary>Returns the ordered hooks for the phase (an empty list when there are none).</summary>
    IReadOnlyList<IAgentHook> HooksFor(AgentPhase phase);

    /// <summary>Runs all hooks of the phase in order.</summary>
    Task<HookOutcome> RunAsync(AgentPhase phase, HookContext ctx, CancellationToken ct);

    /// <summary>Whether a hook has marked this turn to skip the agent (sticky until <see cref="ResetTurn"/>).</summary>
    bool SkipAgent { get; }

    /// <summary>Clears the sticky flag and ordering cache, starting a new turn.</summary>
    void ResetTurn();
}

/// <summary>
/// Default <see cref="IHookRegistry"/> implementation.
/// <para>
/// <b>Ordering</b>: each phase is topologically sorted independently, with edges from
/// <see cref="IAgentHook.Before"/>/<see cref="IAgentHook.After"/>; ties break by
/// <c>(Priority ascending, registration order)</c>.
/// </para>
/// <para>
/// <b>Unknown constraints</b>: a constraint name absent from the phase is recorded in <see cref="Warnings"/> and
/// otherwise ignored, never fatal; each unknown name is recorded once, ordered by phase enum order plus
/// registration order (recomputed after every registration, so the warning disappears once the target hook is
/// registered).
/// </para>
/// <para>
/// <b>Cycles</b>: detected at <see cref="Register"/> time and thrown as <see cref="HookCycleException"/> (the
/// message names the hooks in the cycle); that registration is rolled back and the registry stays usable.
/// </para>
/// <para>
/// <b>Concurrency</b>: <see cref="Register"/> and reads are protected by one lock; the lock never spans
/// <c>await</c>.
/// </para>
/// </summary>
public sealed class HookRegistry : IHookRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<AgentPhase, List<IAgentHook>> _byPhase = new();
    private readonly Dictionary<AgentPhase, IReadOnlyList<IAgentHook>> _sorted = new();
    private readonly List<string> _warnings = new();
    private bool _skipAgent;

    /// <summary>Current accumulated unknown-constraint warnings (one per unknown name).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return _warnings.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool SkipAgent
    {
        get
        {
            lock (_gate)
            {
                return _skipAgent;
            }
        }
    }

    /// <inheritdoc />
    public void Register(IAgentHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        lock (_gate)
        {
            if (!_byPhase.TryGetValue(hook.Phase, out var phaseHooks))
            {
                phaseHooks = new List<IAgentHook>();
                _byPhase[hook.Phase] = phaseHooks;
            }

            foreach (var existing in phaseHooks)
            {
                if (string.Equals(existing.Name, hook.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"钩子名 '{hook.Name}' 已在阶段 {hook.Phase} 注册。");
                }
            }

            // Trial sort first (no warnings): a cycle throws here, before any committed state changes.
            var candidate = new List<IAgentHook>(phaseHooks.Count + 1);
            candidate.AddRange(phaseHooks);
            candidate.Add(hook);
            TopoSort(candidate, hook.Phase, warnings: null);

            phaseHooks.Add(hook);
            RebuildSortedLocked();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentHook> HooksFor(AgentPhase phase)
    {
        lock (_gate)
        {
            if (_sorted.Count == 0)
            {
                RebuildSortedLocked();
            }

            return _sorted.TryGetValue(phase, out var hooks) ? hooks : Array.Empty<IAgentHook>();
        }
    }

    /// <inheritdoc />
    public async Task<HookOutcome> RunAsync(AgentPhase phase, HookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        foreach (var hook in HooksFor(phase))
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await hook.RunAsync(ctx, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case HookOutcome.ShortCircuit:
                    return HookOutcome.ShortCircuit;
                case HookOutcome.SkipAgent:
                    MarkSkipAgent();
                    break;
                default:
                    break;
            }
        }

        return SkipAgent ? HookOutcome.SkipAgent : HookOutcome.Continue;
    }

    /// <inheritdoc />
    public void ResetTurn()
    {
        lock (_gate)
        {
            _skipAgent = false;
            _sorted.Clear();
        }
    }

    private void MarkSkipAgent()
    {
        lock (_gate)
        {
            _skipAgent = true;
        }
    }

    /// <summary>Recomputes the ordering and warning list for all phases (caller must hold the lock).</summary>
    private void RebuildSortedLocked()
    {
        _sorted.Clear();
        _warnings.Clear();

        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Iterate phases in enum order so the Warnings order is deterministic.
        foreach (var phase in Enum.GetValues<AgentPhase>())
        {
            if (!_byPhase.TryGetValue(phase, out var hooks) || hooks.Count == 0)
            {
                _sorted[phase] = Array.Empty<IAgentHook>();
                continue;
            }

            _sorted[phase] = TopoSort(hooks, phase, unknown);
        }

        foreach (var name in unknown)
        {
            if (seen.Add(name))
            {
                _warnings.Add(name);
            }
        }
    }

    /// <summary>
    /// Topological sort: ties break by (Priority ascending, registration order); throws
    /// <see cref="HookCycleException"/> on a cycle. When <paramref name="warnings"/> is non-null, unknown
    /// constraint names are collected there.
    /// </summary>
    private static List<IAgentHook> TopoSort(IReadOnlyList<IAgentHook> hooks, AgentPhase phase, List<string>? warnings)
    {
        var count = hooks.Count;
        var indexByName = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            indexByName[hooks[i].Name] = i;
        }

        var edges = new HashSet<(int From, int To)>();
        for (var i = 0; i < count; i++)
        {
            var hook = hooks[i];

            if (hook.Before is { } before)
            {
                foreach (var name in before)
                {
                    if (indexByName.TryGetValue(name, out var target))
                    {
                        edges.Add((i, target));
                    }
                    else
                    {
                        warnings?.Add(name);
                    }
                }
            }

            if (hook.After is { } after)
            {
                foreach (var name in after)
                {
                    if (indexByName.TryGetValue(name, out var source))
                    {
                        edges.Add((source, i));
                    }
                    else
                    {
                        warnings?.Add(name);
                    }
                }
            }
        }

        var successors = new List<int>[count];
        var predecessors = new List<int>[count];
        var inDegree = new int[count];
        for (var i = 0; i < count; i++)
        {
            successors[i] = new List<int>();
            predecessors[i] = new List<int>();
        }

        foreach (var (from, to) in edges)
        {
            successors[from].Add(to);
            predecessors[to].Add(from);
            inDegree[to]++;
        }

        var ready = new SortedSet<(int Priority, int Index)>();
        for (var i = 0; i < count; i++)
        {
            if (inDegree[i] == 0)
            {
                ready.Add((hooks[i].Priority, i));
            }
        }

        var ordered = new List<IAgentHook>(count);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            var index = next.Index;
            ordered.Add(hooks[index]);

            foreach (var successor in successors[index])
            {
                if (--inDegree[successor] == 0)
                {
                    ready.Add((hooks[successor].Priority, successor));
                }
            }
        }

        if (ordered.Count != count)
        {
            throw new HookCycleException(BuildCycleMessage(hooks, phase, inDegree, predecessors));
        }

        return ordered;
    }

    /// <summary>Walks predecessors from a node still in the queue to find a real cycle and build the message (containing at least one hook name in the cycle).</summary>
    private static string BuildCycleMessage(
        IReadOnlyList<IAgentHook> hooks,
        AgentPhase phase,
        int[] inDegree,
        List<int>[] predecessors)
    {
        var start = 0;
        for (var i = 0; i < hooks.Count; i++)
        {
            if (inDegree[i] > 0)
            {
                start = i;
                break;
            }
        }

        var path = new List<int>();
        var position = new Dictionary<int, int>();
        var current = start;
        while (!position.ContainsKey(current))
        {
            position[current] = path.Count;
            path.Add(current);

            // In-degree still > 0 means a predecessor that is also still in the queue must exist.
            current = predecessors[current].Find(p => inDegree[p] > 0);
        }

        var cycle = path.Skip(position[current]).Select(i => hooks[i].Name).ToList();
        return $"阶段 {phase} 的钩子约束成环：{string.Join(" -> ", cycle)} -> {cycle[0]}";
    }
}
