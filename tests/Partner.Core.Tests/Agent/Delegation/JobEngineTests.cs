using System.Net;
using Partner.Core.Agent;
using Partner.Core.Agent.Delegation;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent.Delegation;

/// <summary>
/// The round loop of a sub-agent job: when it retries, when it stops, and what it tells the main agent.
/// <para>
/// Every "does not retry" test scripts exactly the rounds it expects. A scripted model that runs out of
/// responses fails the round with an internal error, so a loop that retries when it should have stopped
/// shows up as the wrong category rather than as a hang.
/// </para>
/// </summary>
public sealed class JobEngineTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
    private readonly List<JsonlSessionStore> _stores = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static LlmResponse Call(string id, string name, string args = "{}") =>
        new(null, new[] { new LlmToolCall(id, name, args) }, "tool_calls");

    private static LlmResponse Say(string text) => new(text, Array.Empty<LlmToolCall>(), "stop");

    private static LlmResponse Succeed(string id) => Call(
        id, TaskReportTool.ToolName, """{"status":"success","summary":"搞定","evidence":"校验：画面已变化"}""");

    private static LlmResponse Fail(string id, string error, bool retryable = true) => Call(
        id,
        TaskReportTool.ToolName,
        $$"""{"status":"failed","summary":"没做完","error":"{{error}}","retryable":{{(retryable ? "true" : "false")}}}""");

    private (JobEngine Engine, ScriptedLlmClient Llm, JsonlSessionStore Session) Build(
        ILlmClient llm,
        IToolGovernance? governance = null,
        IApprovalHandler? approval = null,
        AgentTurnOptions? options = null,
        Func<string, ToolResult>? act = null)
    {
        var (session, _) = JsonlSessionStore.Open(Path.Combine(_dir, Guid.NewGuid().ToString("n")));
        _stores.Add(session);
        var report = new TaskReportTool();
        var tools = new InMemoryToolRegistry();
        tools.Register(report);
        tools.Register(new FakeTool("act", (args, _) => Task.FromResult(act?.Invoke(args) ?? new ToolResult(true, "ok"))));
        tools.Register(new FakeTool("asked"));
        tools.Register(new FakeTool("forbidden"));

        var runner = new AgentTurnRunner(
            session,
            llm,
            tools,
            governance ?? new AllowListGovernance(new[] { TaskReportTool.ToolName, "act" }, ask: new[] { "asked" }),
            approval ?? new DenyAllApprovalHandler(),
            new HookRegistry(),
            options: options ?? new AgentTurnOptions { SessionId = "job-test" });

        return (new JobEngine(runner, session, report), llm as ScriptedLlmClient ?? new ScriptedLlmClient([]), session);
    }

    private static JobSpec Spec(int rounds = 5, TimeSpan? deadline = null) =>
        new("job-1", "打开记事本", "用户在忙", rounds, deadline);

    [Fact]
    public async Task A_round_that_reports_success_with_evidence_ends_the_job()
    {
        var llm = new ScriptedLlmClient([Call("a", "act"), Succeed("b"), Say("好了")]);
        var (engine, _, _) = Build(llm);

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.Rounds);
        Assert.Equal("搞定", outcome.Summary);
        Assert.Contains("已完成", outcome.ToMessage("job-1", "桌面操控", "打开记事本").Split('\n')[0]);
    }

    [Fact]
    public async Task A_job_that_fails_four_times_and_then_succeeds_reports_round_five()
    {
        var script = new List<LlmResponse>();
        for (var round = 1; round <= 4; round++)
        {
            script.Add(Call($"c{round}", "act"));
            script.Add(Say("我试过了"));
        }

        script.Add(Succeed("last"));
        script.Add(Say("好了"));
        var (engine, _, _) = Build(new ScriptedLlmClient(script));

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(5, outcome.Rounds);
        Assert.Equal(4, outcome.History.Count(round => !round.Success));
    }

    [Fact]
    public async Task Five_failed_rounds_come_back_as_cannot_execute_with_the_last_error()
    {
        var script = new List<LlmResponse>();
        for (var round = 1; round <= 5; round++)
        {
            script.Add(Call($"c{round}", "act", """{"kind":"leftClick"}"""));
            script.Add(Fail($"r{round}", $"第{round}次找不到按钮"));
            script.Add(Say("失败了"));
        }

        var (engine, _, _) = Build(new ScriptedLlmClient(script));

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(JobCategories.RoundsExhausted, outcome.Category);
        Assert.Equal(5, outcome.Rounds);
        Assert.Contains("第5次找不到按钮", outcome.Error);

        var message = outcome.ToMessage("job-1", "桌面操控", "打开记事本");
        Assert.Contains("无法执行", message.Split('\n')[0]);
        Assert.Contains("rounds_exhausted", message);
        Assert.Contains("5/5", message);
        Assert.Contains("act ×5（leftClick 5）", message);
    }

    [Fact]
    public async Task A_denied_approval_ends_the_job_without_a_second_round()
    {
        var llm = new ScriptedLlmClient([Call("a", "asked"), Say("被拒绝了")]);
        var (engine, _, _) = Build(llm);

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(JobCategories.ApprovalDenied, outcome.Category);
        Assert.Equal(1, outcome.Rounds);
    }

    [Fact]
    public async Task A_governance_refusal_ends_the_job_without_a_second_round()
    {
        var llm = new ScriptedLlmClient([Call("a", "forbidden"), Say("不行")]);
        var (engine, _, _) = Build(llm);

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.Equal(JobCategories.GovernanceDenied, outcome.Category);
        Assert.Equal(1, outcome.Rounds);
    }

    [Fact]
    public async Task A_marker_the_host_recognises_ends_the_job()
    {
        var llm = new ScriptedLlmClient([Call("a", "act"), Say("没成")]);
        var (session, _) = JsonlSessionStore.Open(Path.Combine(_dir, "marker"));
        _stores.Add(session);
        var report = new TaskReportTool();
        var tools = new InMemoryToolRegistry();
        tools.Register(report);
        tools.Register(new FakeTool("act", (_, _) => Task.FromResult(new ToolResult(false, "未注入：目标以管理员运行"))));
        var runner = new AgentTurnRunner(
            session, llm, tools, new AllowListGovernance(new[] { "act" }), new DenyAllApprovalHandler(), new HookRegistry());
        var engine = new JobEngine(runner, session, report, body => body.Contains("管理员") ? JobCategories.GovernanceDenied : null);

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.Equal(JobCategories.GovernanceDenied, outcome.Category);
        Assert.Equal(1, outcome.Rounds);
    }

    [Fact]
    public async Task The_sub_agent_can_say_another_round_cannot_help()
    {
        var llm = new ScriptedLlmClient([Fail("a", "文件确实不存在", retryable: false), Say("做不成")]);
        var (engine, _, _) = Build(llm);

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.Equal(JobCategories.ReportedImpossible, outcome.Category);
        Assert.Equal(1, outcome.Rounds);
        Assert.Contains("文件确实不存在", outcome.Error);
    }

    [Fact]
    public async Task One_round_means_no_retry()
    {
        var llm = new ScriptedLlmClient([Say("我没汇报")]);
        var (engine, _, _) = Build(llm);

        var outcome = await engine.RunAsync(Spec(rounds: 1), CancellationToken.None);

        Assert.Equal(JobCategories.RoundsExhausted, outcome.Category);
        Assert.Equal(1, outcome.Rounds);
        Assert.Contains("没有调用 task_report", outcome.History[0].Reason);
    }

    [Fact]
    public async Task A_spent_step_budget_is_a_failed_round_and_says_so()
    {
        var llm = new ScriptedLlmClient([Call("a", "act"), Call("b", "act")]);
        var (engine, _, _) = Build(llm, options: new AgentTurnOptions { MaxSteps = 2, SessionId = "job-test" });

        var outcome = await engine.RunAsync(Spec(rounds: 1), CancellationToken.None);

        Assert.Contains("步数耗尽", outcome.History[0].Reason);
    }

    [Fact]
    public async Task The_next_round_is_told_what_failed_and_what_was_already_done()
    {
        var llm = new ScriptedLlmClient([
            Call("a", "act", """{"kind":"typeText"}"""), Say("没成"),
            Succeed("b"), Say("好了")]);
        var (engine, scripted, _) = Build(llm);

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.True(outcome.Completed);
        var second = llm.Requests[2].Messages.Last(message => message.Role == "user").Content!;
        Assert.Contains("上一轮（第 1 轮）没有成功", second);
        Assert.Contains("act ×1（typeText 1）", second);
        Assert.Contains("不要重复已经完成的动作", second);
        Assert.Contains("打开记事本", second);
        _ = scripted;
    }

    [Fact]
    public async Task A_cancelled_job_says_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        var (engine, _, _) = Build(new BlockingLlmClient());

        var running = engine.RunAsync(Spec(), cancel.Token);
        await Task.Delay(100);
        cancel.Cancel();

        var outcome = await running;
        Assert.Equal(JobCategories.Cancelled, outcome.Category);
    }

    [Fact]
    public async Task The_whole_job_has_a_deadline()
    {
        var (engine, _, _) = Build(new BlockingLlmClient());

        var outcome = await engine.RunAsync(Spec(deadline: TimeSpan.FromMilliseconds(150)), CancellationToken.None);

        Assert.Equal(JobCategories.Timeout, outcome.Category);
    }

    [Fact]
    public async Task A_failed_model_call_is_terminal()
    {
        var failure = ModelCallFailure.Rejected("补全", HttpStatusCode.Unauthorized, "bad key", null);
        var (engine, _, _) = Build(new ThrowingLlmClient("x", failure));

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.Equal(JobCategories.ModelError, outcome.Category);
        Assert.Equal(1, outcome.Rounds);
    }

    [Fact]
    public async Task Success_without_evidence_is_refused_by_the_tool()
    {
        var report = new TaskReportTool();

        var result = await report.InvokeAsync("""{"status":"success","summary":"好了"}""");

        Assert.False(result.Ok);
        Assert.Contains("evidence", result.Content);
        Assert.Null(report.Latest);
    }

    [Fact]
    public async Task A_new_round_does_not_inherit_the_last_rounds_report()
    {
        var report = new TaskReportTool();
        await report.InvokeAsync("""{"status":"success","summary":"好了","evidence":"x"}""");
        Assert.NotNull(report.Latest);

        report.Reset();

        Assert.Null(report.Latest);
    }

    /// <summary>A model call that never returns on its own but stops when told to, and says that it was told.</summary>
    private sealed class ListeningHangLlm : ILlmClient
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_round_that_overruns_is_cancelled_and_has_stopped_writing_before_the_job_reports()
    {
        var llm = new ListeningHangLlm();
        var (session, _) = JsonlSessionStore.Open(Path.Combine(_dir, Guid.NewGuid().ToString("n")));
        _stores.Add(session);
        var report = new TaskReportTool();
        var tools = new InMemoryToolRegistry();
        tools.Register(report);

        var runner = new AgentTurnRunner(
            session,
            llm,
            tools,
            new AllowListGovernance(new[] { TaskReportTool.ToolName }),
            new DenyAllApprovalHandler(),
            new HookRegistry(),
            options: new AgentTurnOptions { SessionId = "job-test", TurnTimeout = TimeSpan.FromMilliseconds(150) });
        var engine = new JobEngine(runner, session, report, cancelGrace: TimeSpan.FromSeconds(5));

        var outcome = await engine.RunAsync(Spec(), CancellationToken.None);

        Assert.Equal(JobCategories.Timeout, outcome.Category);

        // The abandoned round was told to stop — not merely left running behind the report.
        Assert.True(llm.Cancelled.Task.IsCompleted, "the overrunning round was never cancelled");

        // And it is quiet: nothing is appended to the job's session after the outcome was returned.
        var before = await CountEventsAsync(session);
        await Task.Delay(300);
        Assert.Equal(before, await CountEventsAsync(session));
    }

    private static async Task<int> CountEventsAsync(ISessionStore store)
    {
        var count = 0;
        await foreach (var _ in store.ReadAllAsync())
        {
            count++;
        }

        return count;
    }
}
