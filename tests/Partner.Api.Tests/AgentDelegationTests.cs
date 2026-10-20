using System.Net.Http.Json;
using System.Text.Json;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// Routes a request to the script of whichever agent sent it. The main agent and a sub-agent share one model
/// client when the sub-agent follows the main model, so a single script would be consumed in whatever order the
/// two happen to run; telling them apart by their system prompt keeps each test deterministic.
/// </summary>
internal sealed class RoutingLlm : ILlmClient
{
    public ScriptedLlmClient Main { get; set; } = new([]);

    public ILlmClient Sub { get; set; } = new ScriptedLlmClient([]);

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var system = request.Messages.FirstOrDefault()?.Content ?? string.Empty;
        return system.Contains("## 汇报方式", StringComparison.Ordinal)
            ? Sub.CompleteAsync(request, ct)
            : Main.CompleteAsync(request, ct);
    }
}

/// <summary>A sub-agent model that does not answer until the test lets it.</summary>
internal sealed class GatedLlm(ScriptedLlmClient inner) : ILlmClient
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return await inner.CompleteAsync(request, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The whole delegation path with a scripted model: the main agent hands work over, the sub-agent works in its own
/// session, and a report — success or "cannot execute" — comes back as a new turn.
/// </summary>
public sealed class AgentDelegationTests : ApiFixture, IDisposable
{
    private readonly RoutingLlm _llm = new();
    private string _data = string.Empty;

    public void Dispose()
    {
        if (_data.Length > 0 && Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    protected override string WorkspaceName => "partner-delegation-tests";

    protected override ILlmClient? Llm => _llm;

    protected override string ReadinessPath => "/api/session";

    private static LlmResponse Call(string id, string name, string args) =>
        new(null, [new LlmToolCall(id, name, args)], "tool_calls");

    private static LlmResponse Say(string text) => new(text, [], "stop");

    private static LlmResponse Report(string id, bool success, string text) => Call(
        id,
        "task_report",
        success
            ? $$"""{"status":"success","summary":"{{text}}","evidence":"a.txt 全文"}"""
            : $$"""{"status":"failed","summary":"没做完","error":"{{text}}"}""");

    private static LlmResponse Delegate(string id, string goal = "读取 a.txt 并说出内容") =>
        Call(id, "agent_delegate", $$"""{"scenario":"file-read","goal":"{{goal}}"}""");

    /// <summary>Turns on file reading under a temp directory and adds the file-read sub-agent.</summary>
    private async Task EnableReaderAsync(int maxRounds = 5, bool withReader = true)
    {
        // Outside the workspace on purpose: the workspace itself is refused to the file tools, wherever the roots are.
        _data = Path.Combine(Path.GetTempPath(), "partner-delegation-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_data);
        await File.WriteAllTextAsync(Path.Combine(_data, "a.txt"), "你好，世界");

        var settings = _context.Settings.Load();
        var agents = withReader
            ? settings.Agents with
            {
                SubAgents =
                [
                    .. settings.Agents.SubAgents,
                    new SubAgentSettings { Id = "reader", Name = "文件阅读", Scenario = AgentScenarios.FileRead, MaxRounds = maxRounds },
                ],
            }
            : settings.Agents;

        // Written directly, not through PUT /api/settings: that endpoint rebuilds the consumer from the stored
        // model section, and this host has no stored model — only the scripted one injected below.
        _context.Settings.Save(settings with { Files = new FilesSettings { Enabled = true, Roots = [_data] }, Agents = agents });
        await _consumer.ReconfigureAsync(config: null, "test", _llm);
    }

    private async Task<List<SessionEvent>> EventsAsync()
    {
        var events = new List<SessionEvent>();
        await foreach (var logged in _context.Session.ReadAllAsync())
        {
            events.Add(logged);
        }

        return events;
    }

    private async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"timed out waiting for: {what}");
    }

    private Task WaitForAssistantAsync(string contains) => WaitAsync(
        async () => (await EventsAsync()).Any(e => e.Kind == SessionEventKind.Assistant && e.Body.Contains(contains, StringComparison.Ordinal)),
        $"assistant saying '{contains}'");

    private Task SendAsync(string text) => _http.PostAsJsonAsync("/api/input", new { source = "Text", payload = text });

    [Fact]
    public async Task A_delegated_job_reports_back_as_a_new_turn_and_stays_out_of_the_main_session()
    {
        await EnableReaderAsync();
        _llm.Main = new ScriptedLlmClient([Delegate("m1"), Say("已交给文件阅读"), Say("文件里写的是：你好，世界")]);
        _llm.Sub = new ScriptedLlmClient([
            Call("s1", "file_read", """{"path":"a.txt"}"""),
            Report("s2", true, "文件内容：你好，世界"),
            Say("好了")]);

        await SendAsync("帮我读一下 a.txt");
        await WaitForAssistantAsync("文件里写的是");

        var events = await EventsAsync();

        // The report came back from the agent source, first line saying how it ended.
        var report = events.Single(e => e.Kind == SessionEventKind.User && e.Source == InputSource.Agent);
        Assert.StartsWith("[子 agent「文件阅读」任务", report.Body, StringComparison.Ordinal);
        Assert.Contains("已完成", report.Body.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("文件内容：你好，世界", report.Body, StringComparison.Ordinal);

        // The main agent saw the delegation and the report; it never called a file tool itself.
        var called = events.Where(e => e.Kind == SessionEventKind.ToolCall).Select(e => e.Meta!["name"]).ToList();
        Assert.Equal(["agent_delegate"], called);
        Assert.Contains("已完成", _llm.Main.Requests[2].Messages.Last(m => m.Role == "user").Content!, StringComparison.Ordinal);

        // The work is in the job's own session.
        var job = Assert.Single(_consumer.Agents.Jobs());
        Assert.Equal("completed", job.Status);
        // (The log escapes non-ASCII text, so it is read back as JSON rather than searched as text.)
        var jobLog = (await File.ReadAllLinesAsync(Path.Combine(_workspace, "agents", "reader", job.Id, "session", "events.jsonl")))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("body").GetString() ?? string.Empty)
            .ToList();
        Assert.Contains(jobLog, body => body.Contains("你好，世界", StringComparison.Ordinal) && body.Contains("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Five_failed_rounds_come_back_as_cannot_execute_with_the_reason()
    {
        await EnableReaderAsync();
        var sub = new List<LlmResponse>();
        for (var round = 1; round <= 5; round++)
        {
            sub.Add(Report($"r{round}", false, $"没找到 a.txt（第{round}次）"));
            sub.Add(Say("失败了"));
        }

        _llm.Main = new ScriptedLlmClient([Delegate("m1"), Say("交出去了"), Say("抱歉，读不到：没找到 a.txt")]);
        _llm.Sub = new ScriptedLlmClient(sub);

        await SendAsync("帮我读一下 a.txt");
        await WaitForAssistantAsync("抱歉，读不到");

        var report = (await EventsAsync()).Single(e => e.Kind == SessionEventKind.User && e.Source == InputSource.Agent);
        Assert.Contains("无法执行", report.Body.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("rounds_exhausted", report.Body, StringComparison.Ordinal);
        Assert.Contains("5/5", report.Body, StringComparison.Ordinal);
        Assert.Contains("没找到 a.txt（第5次）", report.Body, StringComparison.Ordinal);

        // The main agent was handed the reason and the instruction not to call it done.
        Assert.Contains("不要说成已经完成", _llm.Main.Requests[0].Messages[0].Content!, StringComparison.Ordinal);
        Assert.Equal(0, ((ScriptedLlmClient)_llm.Sub).Remaining);
        Assert.Equal("cannot_execute", Assert.Single(_consumer.Agents.Jobs()).Status);
    }

    [Fact]
    public async Task A_busy_agent_queues_two_more_jobs_and_refuses_the_next_with_the_newest_named()
    {
        await EnableReaderAsync();
        var gate = new GatedLlm(new ScriptedLlmClient([Report("s1", true, "好了"), Say("ok")]));
        _llm.Sub = gate;
        _llm.Main = new ScriptedLlmClient([
            new LlmResponse(null, [new LlmToolCall("m1", "agent_delegate", """{"scenario":"file-read","goal":"甲"}"""),
                new LlmToolCall("m2", "agent_delegate", """{"scenario":"file-read","goal":"乙"}"""),
                new LlmToolCall("m3", "agent_delegate", """{"scenario":"file-read","goal":"丙"}"""),
                new LlmToolCall("m4", "agent_delegate", """{"scenario":"file-read","goal":"丁"}""")], "tool_calls"),
            Say("一个在做，两个在排队，一个被拒"),
            Say("报告收到")]);

        await SendAsync("读四个");
        await WaitForAssistantAsync("一个在做");

        var results = (await EventsAsync()).Where(e => e.Kind == SessionEventKind.ToolResult).ToList();
        Assert.Equal(4, results.Count);

        // The first starts at once; the next two are accepted and wait their turn.
        Assert.Single(results, e => e.Body.Contains("已开始", StringComparison.Ordinal));
        Assert.Equal(2, results.Count(e => e.Meta!["ok"] == "true" && e.Body.Contains("在排队", StringComparison.Ordinal)));

        // The fourth finds the queue full.
        var busy = Assert.Single(results, e => e.Meta!["ok"] == "false");
        Assert.StartsWith("busy:", busy.Body, StringComparison.Ordinal);

        // The status tool shows the waiting ones as queued, not as running.
        Assert.Equal(2, _consumer.Agents.Jobs().Count(job => job.Status == "queued"));

        gate.Release();
    }

    [Fact]
    public async Task A_sub_agent_is_given_the_last_few_turns_so_that_references_can_be_resolved()
    {
        _llm.Main = new ScriptedLlmClient([Say("我看看那份报告。")]);

        await SendAsync("把刚才那个文件读完");
        await WaitForAssistantAsync("我看看");

        var background = await _consumer.Agents.RecentConversationAsync();

        Assert.NotNull(background);
        Assert.Contains("用户：把刚才那个文件读完", background, StringComparison.Ordinal);
        Assert.Contains("助手：我看看那份报告。", background, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_goal_is_not_handed_over_again_right_after_it_failed()
    {
        await EnableReaderAsync(maxRounds: 1);
        _llm.Main = new ScriptedLlmClient([Say("收到失败报告")]);
        _llm.Sub = new ScriptedLlmClient([Report("s1", false, "读不到"), Say("失败")]);

        var first = _consumer.Agents.Start("file-read", "读取 a.txt", null);
        Assert.True(first.Started);
        await WaitAsync(() => Task.FromResult(_consumer.Agents.Job(first.Job!.Id)!.Status != "running"), "the job to end");
        await WaitForAssistantAsync("收到失败报告");

        // Different spacing and punctuation, same goal.
        var again = _consumer.Agents.Start("file-read", " 读取a.txt！", null);

        Assert.False(again.Started);
        Assert.Contains("刚刚失败过", again.Refusal, StringComparison.Ordinal);

        // A genuinely different goal goes through.
        _llm.Sub = new ScriptedLlmClient([Report("s2", true, "好了"), Say("ok")]);
        _llm.Main = new ScriptedLlmClient([Say("第二个报告")]);
        Assert.True(_consumer.Agents.Start("file-read", "列出目录里有什么", null).Started);
        await WaitForAssistantAsync("第二个报告");
    }

    [Fact]
    public async Task Cancelling_a_job_reports_cancelled()
    {
        await EnableReaderAsync();
        _llm.Sub = new GatedLlm(new ScriptedLlmClient([]));
        _llm.Main = new ScriptedLlmClient([Say("知道了，已取消")]);

        var started = _consumer.Agents.Start("file-read", "读取 a.txt", null);
        Assert.True(_consumer.Agents.Cancel(started.Job!.Id));
        await WaitForAssistantAsync("已取消");

        var report = (await EventsAsync()).Single(e => e.Kind == SessionEventKind.User && e.Source == InputSource.Agent);
        Assert.Contains("无法执行", report.Body.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("cancelled", report.Body, StringComparison.Ordinal);
        Assert.False(_consumer.Agents.Cancel(started.Job.Id));
    }

    [Fact]
    public async Task A_job_the_process_died_under_is_reported_once_on_the_next_start()
    {
        await EnableReaderAsync();
        _llm.Main = new ScriptedLlmClient([Say("明白，任务被中断了")]);
        Directory.CreateDirectory(Path.Combine(_workspace, "agents"));
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "agents", "jobs.jsonl"),
            """{"event":"started","id":"job-dead","agentName":"文件阅读","scenario":"file-read","goal":"读取 a.txt","at":"2026-01-01T00:00:00+00:00"}"""
            + "\n");

        Assert.Equal(1, await _consumer.Agents.RecoverAsync());
        await WaitForAssistantAsync("任务被中断了");

        var report = (await EventsAsync()).Single(e => e.Kind == SessionEventKind.User && e.Source == InputSource.Agent);
        Assert.Contains("interrupted", report.Body, StringComparison.Ordinal);

        // The journal now says it finished, so a second start finds nothing to report.
        Assert.Equal(0, await _consumer.Agents.RecoverAsync());
    }

    [Fact]
    public async Task Tools_have_one_holder_the_sub_agent_when_there_is_one_else_the_main_agent()
    {
        async Task<(List<string> Main, List<string> Sub)> ToolsAsync()
        {
            var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
            var main = state.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
            var sub = state.GetProperty("agentTools").EnumerateArray()
                .SelectMany(agent => agent.GetProperty("tools").EnumerateArray())
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
            return (main, sub);
        }

        // The web capability ships enabled and no sub-agent has claimed it, so the main agent holds its two tools;
        // the timed-task tools are always the main agent's. Nothing else.
        var (fresh, _) = await ToolsAsync();
        Assert.Equal(
            new[] { "schedule_create", "schedule_delete", "schedule_list", "web_fetch", "web_search" },
            fresh.OrderBy(name => name, StringComparer.Ordinal));

        // Files on, no file-read agent: "no scenario configured" means the main agent reads. The web tools are
        // untouched, because each scenario has its own holder rather than one shared pool.
        await EnableReaderAsync(withReader: false);
        var (mainOnly, noSub) = await ToolsAsync();
        Assert.Contains("file_read", mainOnly);
        Assert.Contains("web_search", mainOnly);
        Assert.DoesNotContain("agent_delegate", mainOnly);
        Assert.Empty(noSub);

        // A file-read agent takes them over, and the main agent gets the delegation tools instead.
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Agents = settings.Agents with
            {
                SubAgents = [.. settings.Agents.SubAgents, new SubAgentSettings { Id = "reader", Name = "文件阅读", Scenario = AgentScenarios.FileRead }],
            },
        });
        await _consumer.ReconfigureAsync(config: null, "test", _llm);

        var (mainDelegating, sub) = await ToolsAsync();
        Assert.DoesNotContain("file_read", mainDelegating);
        Assert.Contains("agent_delegate", mainDelegating);
        Assert.Contains("web_search", mainDelegating);
        Assert.Contains("file_read", sub);
        Assert.Contains("file_list", sub);

        // The same rule for the web scenario, and the other half of it: the moment an enabled sub-agent owns it,
        // the main agent stops searching. "Enabled" is the whole condition — a disabled one holds nothing.
        settings = _context.Settings.Load();
        _context.Settings.Save(settings with
        {
            Agents = settings.Agents with
            {
                SubAgents = [.. settings.Agents.SubAgents, new SubAgentSettings { Id = "searcher", Name = "网络搜索", Scenario = AgentScenarios.WebSearch }],
            },
        });
        await _consumer.ReconfigureAsync(config: null, "test", _llm);

        var (mainAfter, withSearcher) = await ToolsAsync();
        Assert.DoesNotContain("web_search", mainAfter);
        Assert.DoesNotContain("web_fetch", mainAfter);
        Assert.Contains("agent_delegate", mainAfter);

        // And the searcher is reported with the tools of its own scenario, not with the file ones.
        Assert.Equal(
            new[] { "web_fetch", "web_search" },
            withSearcher.Except(sub).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Editing_the_main_prompt_reaches_the_next_turn_without_restarting_the_consumer()
    {
        _llm.Main = new ScriptedLlmClient([Say("一"), Say("二")]);

        await SendAsync("第一句");
        await WaitForAssistantAsync("一");
        Assert.Equal("You are a single-user local assistant.", _llm.Main.Requests[0].Messages[0].Content);

        // The reply is in the log a moment before the loop counts the turn as handled; wait for the count, or the
        // "nothing restarted" comparison below races it.
        Assert.True(await Eventually.IsTrueAsync(() => Task.FromResult(_consumer.State.Stats.Handled >= 1)));

        var handledBefore = _consumer.State.Stats.Handled;
        var saved = await _http.PutAsJsonAsync("/api/agents", new
        {
            main = new { prompt = "你是小克，说话简短。" },
            subAgents = new[] { new { id = "desktop", name = "桌面操控", enabled = true, scenario = "desktop", maxRounds = 5 } },
        });
        saved.EnsureSuccessStatusCode();

        // Nothing restarted: the counters of the running consumer are still there.
        Assert.Equal(handledBefore, _consumer.State.Stats.Handled);

        await SendAsync("第二句");
        await WaitForAssistantAsync("二");
        Assert.Equal("你是小克，说话简短。", _llm.Main.Requests[1].Messages[0].Content);
    }

    [Fact]
    public async Task A_sub_agent_report_is_not_distilled_into_memory_as_something_the_person_said()
    {
        await EnableReaderAsync();
        _llm.Main = new ScriptedLlmClient([Say("收到")]);
        _llm.Sub = new ScriptedLlmClient([Report("s1", true, "用户的生日是三月五日，用户喜欢吃辣"), Say("好了")]);

        Assert.True(_consumer.Agents.Start("file-read", "读取 a.txt", null).Started);
        await WaitForAssistantAsync("收到");

        // Distillation runs at the end of the turn; give it the moment it needs, then look.
        await Task.Delay(300);
        Assert.Empty(await _context.Memory.ListAsync());
    }
}
