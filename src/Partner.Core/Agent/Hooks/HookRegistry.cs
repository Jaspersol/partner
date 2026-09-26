namespace Partner.Core.Agent.Hooks;

/// <summary>
/// 钩子注册表（设计文档 §2.9.3）：注册、按阶段排序、按阶段执行。
/// </summary>
public interface IHookRegistry
{
    /// <summary>
    /// 注册一个钩子。同一阶段内 <see cref="IAgentHook.Name"/> 重复或约束成环时抛异常，且注册不生效。
    /// </summary>
    void Register(IAgentHook hook);

    /// <summary>返回该阶段已排序的钩子列表（无钩子时返回空列表）。</summary>
    IReadOnlyList<IAgentHook> HooksFor(AgentPhase phase);

    /// <summary>按顺序执行该阶段的所有钩子。</summary>
    Task<HookOutcome> RunAsync(AgentPhase phase, HookContext ctx, CancellationToken ct);

    /// <summary>本轮是否已被某个钩子标记为跳过 Agent（粘性，直到 <see cref="ResetTurn"/>）。</summary>
    bool SkipAgent { get; }

    /// <summary>清除粘性标志与排序缓存，开始新的一轮。</summary>
    void ResetTurn();
}

/// <summary>
/// <see cref="IHookRegistry"/> 的默认实现（设计文档 §2.9.3）。
/// <para>
/// <b>排序</b>：每个阶段独立做拓扑排序，边来自 <see cref="IAgentHook.Before"/>/<see cref="IAgentHook.After"/>；
/// 同时可执行的钩子按 <c>(Priority 升序, 注册顺序)</c> 决胜。
/// </para>
/// <para>
/// <b>未知约束</b>：约束名在同一阶段内不存在时只记录到 <see cref="Warnings"/> 并忽略，绝不失败；
/// 每个未知名字只记一条，顺序由阶段枚举顺序 + 注册顺序决定（每次注册后重算，因此目标钩子后续注册时该条警告会消失）。
/// </para>
/// <para>
/// <b>成环</b>：在 <see cref="Register"/> 时即检出并抛 <see cref="HookCycleException"/>（消息含成环钩子名），
/// 该次注册回滚，注册表保持可用。
/// </para>
/// <para>
/// <b>并发</b>：<see cref="Register"/> 与读取均由同一把锁保护；锁不跨 <c>await</c>。
/// </para>
/// </summary>
public sealed class HookRegistry : IHookRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<AgentPhase, List<IAgentHook>> _byPhase = new();
    private readonly Dictionary<AgentPhase, IReadOnlyList<IAgentHook>> _sorted = new();
    private readonly List<string> _warnings = new();
    private bool _skipAgent;

    /// <summary>当前累计的未知约束警告（每个未知名字一条）。</summary>
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

            // 先试排（不产生警告）：成环则直接抛，且不改变任何已提交状态。
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

    /// <summary>重算所有阶段的排序结果与警告列表（调用方须持锁）。</summary>
    private void RebuildSortedLocked()
    {
        _sorted.Clear();
        _warnings.Clear();

        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 按枚举顺序遍历阶段，保证 Warnings 的顺序确定。
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
    /// 拓扑排序：同时可执行的钩子按 (Priority 升序, 注册顺序) 决胜；成环时抛 <see cref="HookCycleException"/>。
    /// <paramref name="warnings"/> 非 null 时收集未知约束名。
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

    /// <summary>从仍未出队的节点沿前驱回溯，定位一个真实的环并生成消息（至少含一个成环钩子名）。</summary>
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

            // 入度仍大于 0 ⇒ 必然存在一个同样未出队的前驱。
            current = predecessors[current].Find(p => inDegree[p] > 0);
        }

        var cycle = path.Skip(position[current]).Select(i => hooks[i].Name).ToList();
        return $"阶段 {phase} 的钩子约束成环：{string.Join(" -> ", cycle)} -> {cycle[0]}";
    }
}
