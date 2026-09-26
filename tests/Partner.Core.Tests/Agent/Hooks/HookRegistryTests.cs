using Partner.Core.Agent.Hooks;
using Partner.Core.Contracts;

namespace Partner.Core.Tests.Agent.Hooks;

/// <summary>
/// 钩子注册表（设计文档 §2.9.3）：排序、约束、短路、粘性跳过、注入与取消。
/// </summary>
public sealed class HookRegistryTests
{
    [Fact]
    public async Task HooksFor_OrdersByPriorityAscending()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("p30", AgentPhase.PreDispatch, log, priority: 30));
        registry.Register(Hook("p10", AgentPhase.PreDispatch, log, priority: 10));
        registry.Register(Hook("p20", AgentPhase.PreDispatch, log, priority: 20));

        Assert.Equal(new[] { "p10", "p20", "p30" }, registry.HooksFor(AgentPhase.PreDispatch).Select(h => h.Name));

        var outcome = await registry.RunAsync(AgentPhase.PreDispatch, NewContext(), CancellationToken.None);

        Assert.Equal(HookOutcome.Continue, outcome);
        Assert.Equal(new[] { "p10", "p20", "p30" }, log);
    }

    [Fact]
    public async Task HooksFor_TieBreaksByRegistrationOrder()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("first", AgentPhase.PreExecute, log));
        registry.Register(Hook("second", AgentPhase.PreExecute, log));
        registry.Register(Hook("third", AgentPhase.PreExecute, log));

        Assert.Equal(new[] { "first", "second", "third" }, registry.HooksFor(AgentPhase.PreExecute).Select(h => h.Name));

        await registry.RunAsync(AgentPhase.PreExecute, NewContext(), CancellationToken.None);

        Assert.Equal(new[] { "first", "second", "third" }, log);
    }

    [Fact]
    public async Task HooksFor_HonoursBeforeAndAfterConstraints()
    {
        // Before：B 先注册，但 A 声明必须先于 B，因此 A 排到前面。
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("B", AgentPhase.PreDispatch, log));
        registry.Register(Hook("A", AgentPhase.PreDispatch, log, before: new[] { "B" }));

        Assert.Equal(new[] { "A", "B" }, registry.HooksFor(AgentPhase.PreDispatch).Select(h => h.Name));

        await registry.RunAsync(AgentPhase.PreDispatch, NewContext(), CancellationToken.None);
        Assert.Equal(new[] { "A", "B" }, log);

        // After：X 先注册并声明晚于 Y，因此 Y 排到前面。
        var afterLog = new List<string>();
        var afterRegistry = new HookRegistry();
        afterRegistry.Register(Hook("X", AgentPhase.PreExecute, afterLog, after: new[] { "Y" }));
        afterRegistry.Register(Hook("Y", AgentPhase.PreExecute, afterLog));

        Assert.Equal(new[] { "Y", "X" }, afterRegistry.HooksFor(AgentPhase.PreExecute).Select(h => h.Name));

        await afterRegistry.RunAsync(AgentPhase.PreExecute, NewContext(), CancellationToken.None);
        Assert.Equal(new[] { "Y", "X" }, afterLog);
    }

    [Fact]
    public void Register_ThrowsHookCycleException_WhenConstraintsFormCycle()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("A", AgentPhase.PreDispatch, log, before: new[] { "B" }));

        var ex = Assert.Throws<HookCycleException>(
            () => registry.Register(Hook("B", AgentPhase.PreDispatch, log, before: new[] { "A" })));

        Assert.True(
            ex.Message.Contains("A", StringComparison.Ordinal) || ex.Message.Contains("B", StringComparison.Ordinal),
            $"异常消息未提及成环钩子名：{ex.Message}");

        // 失败的注册被回滚，注册表仍可用。
        Assert.Equal(new[] { "A" }, registry.HooksFor(AgentPhase.PreDispatch).Select(h => h.Name));
    }

    [Fact]
    public async Task UnknownConstraint_IsWarnedAndIgnored()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook(
            "A",
            AgentPhase.PreDispatch,
            log,
            before: new[] { "Ghost" },
            after: new[] { "Phantom" }));

        Assert.Contains("Ghost", registry.Warnings);
        Assert.Contains("Phantom", registry.Warnings);
        Assert.Equal(2, registry.Warnings.Count);

        var outcome = await registry.RunAsync(AgentPhase.PreDispatch, NewContext(), CancellationToken.None);

        Assert.Equal(HookOutcome.Continue, outcome);
        Assert.Equal(new[] { "A" }, log);
    }

    [Fact]
    public async Task ShortCircuit_StopsPhaseImmediately()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("A", AgentPhase.PreExecute, log, outcome: HookOutcome.ShortCircuit));
        registry.Register(Hook("B", AgentPhase.PreExecute, log));

        var outcome = await registry.RunAsync(AgentPhase.PreExecute, NewContext(), CancellationToken.None);

        Assert.Equal(HookOutcome.ShortCircuit, outcome);
        Assert.Equal(new[] { "A" }, log);
    }

    [Fact]
    public async Task SkipAgent_IsStickyButDoesNotStopThePhase()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("A", AgentPhase.PreExecute, log, outcome: HookOutcome.SkipAgent));
        registry.Register(Hook("B", AgentPhase.PreExecute, log));

        Assert.False(registry.SkipAgent);

        var outcome = await registry.RunAsync(AgentPhase.PreExecute, NewContext(), CancellationToken.None);

        Assert.Equal(HookOutcome.SkipAgent, outcome);
        Assert.Equal(new[] { "A", "B" }, log);
        Assert.True(registry.SkipAgent);

        registry.ResetTurn();

        Assert.False(registry.SkipAgent);
        Assert.Equal(new[] { "A", "B" }, registry.HooksFor(AgentPhase.PreExecute).Select(h => h.Name));

        // 重置后仅 Continue 的钩子仍返回 Continue。
        var continueLog = new List<string>();
        var continueRegistry = new HookRegistry();
        continueRegistry.Register(Hook("only", AgentPhase.PreExecute, continueLog));

        Assert.Equal(
            HookOutcome.Continue,
            await continueRegistry.RunAsync(AgentPhase.PreExecute, NewContext(), CancellationToken.None));
    }

    [Fact]
    public void Inject_OrdersByPriorityThenInsertionOrder()
    {
        var ctx = NewContext();
        ctx.Inject("p100", 100);
        ctx.Inject("p10-first", 10);
        ctx.Inject("p10-second", 10);
        ctx.Inject("default-priority");

        Assert.Equal(
            new[] { "p10-first", "p10-second", "p100", "default-priority" },
            ctx.InjectedPrompts);

        Assert.Throws<ArgumentException>(() => ctx.Inject(string.Empty));
        Assert.Throws<ArgumentException>(() => ctx.Inject("   "));

        // 失败的注入不改变已有内容。
        Assert.Equal(4, ctx.InjectedPrompts.Count);
    }

    [Fact]
    public async Task PhasesAreIsolated()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("pre", AgentPhase.PreExecute, log));
        registry.Register(Hook("post", AgentPhase.PostResponse, log));

        Assert.Empty(registry.HooksFor(AgentPhase.PreDispatch));

        await registry.RunAsync(AgentPhase.PreExecute, NewContext(), CancellationToken.None);
        Assert.Equal(new[] { "pre" }, log);

        await registry.RunAsync(AgentPhase.PostResponse, NewContext(), CancellationToken.None);
        Assert.Equal(new[] { "pre", "post" }, log);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("A", AgentPhase.PreExecute, log));

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.RunAsync(AgentPhase.PreExecute, NewContext(), preCancelled.Token));
        Assert.Empty(log);

        // 钩子中途取消：取消穿透到调用方，后续钩子不再运行。
        var midLog = new List<string>();
        var midRegistry = new HookRegistry();
        using var midCancelled = new CancellationTokenSource();
        midRegistry.Register(new Probe(
            "cancel",
            AgentPhase.PreExecute,
            priority: 100,
            before: null,
            after: null,
            midLog,
            _ =>
            {
                midCancelled.Cancel();
                return ValueTask.FromResult(HookOutcome.Continue);
            }));
        midRegistry.Register(Hook("next", AgentPhase.PreExecute, midLog));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => midRegistry.RunAsync(AgentPhase.PreExecute, NewContext(), midCancelled.Token));
        Assert.Equal(new[] { "cancel" }, midLog);
    }

    [Fact]
    public void Register_RejectsDuplicateNameInSamePhase()
    {
        var log = new List<string>();
        var registry = new HookRegistry();
        registry.Register(Hook("dup", AgentPhase.PreExecute, log));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Hook("dup", AgentPhase.PreExecute, log)));

        // 不同阶段可以同名。
        registry.Register(Hook("dup", AgentPhase.PostResponse, log));
        Assert.Equal(new[] { "dup" }, registry.HooksFor(AgentPhase.PostResponse).Select(h => h.Name));
    }

    private static HookContext NewContext() => new()
    {
        SessionId = "session-1",
        Source = InputSource.Text,
        InputPayload = "hello",
        WorkspaceDirectory = "D:/ws",
    };

    private static Probe Hook(
        string name,
        AgentPhase phase,
        List<string> log,
        int priority = 100,
        string[]? before = null,
        string[]? after = null,
        HookOutcome outcome = HookOutcome.Continue)
        => new(name, phase, priority, before, after, log, _ => ValueTask.FromResult(outcome));

    /// <summary>记录调用顺序的测试钩子。</summary>
    private sealed class Probe : AgentHookBase
    {
        private readonly List<string> _log;
        private readonly Func<HookContext, ValueTask<HookOutcome>>? _run;

        public Probe(
            string name,
            AgentPhase phase,
            int priority,
            string[]? before,
            string[]? after,
            List<string> log,
            Func<HookContext, ValueTask<HookOutcome>>? run = null)
        {
            Name = name;
            Phase = phase;
            Priority = priority;
            Before = before ?? Array.Empty<string>();
            After = after ?? Array.Empty<string>();
            _log = log;
            _run = run;
        }

        public override AgentPhase Phase { get; }

        public override string Name { get; }

        public override int Priority { get; }

        public override IReadOnlyList<string> Before { get; }

        public override IReadOnlyList<string> After { get; }

        public override ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
        {
            _log.Add(Name);
            return _run is null ? ValueTask.FromResult(HookOutcome.Continue) : _run(ctx);
        }
    }
}
