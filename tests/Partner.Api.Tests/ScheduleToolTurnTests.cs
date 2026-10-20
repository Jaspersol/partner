using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Scheduler;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// The whole path with a scripted model: the assistant asks to create a timed task, a human is asked, and the task
/// exists only if they said yes. This is the check that registration, governance and the approval queue agree —
/// the unit tests of each cannot show that a tool the model can name is one that can actually run.
/// </summary>
public sealed class ScheduleToolTurnTests : ApiFixture
{
    private ScriptedLlmClient _llm = new([]);

    protected override string WorkspaceName => "partner-schedule-turn-tests";

    protected override ILlmClient? Llm => _llm;

    protected override string ReadinessPath => "/api/session";

    public ScheduleToolTurnTests()
    {
        _llm = new ScriptedLlmClient(
        [
            new LlmResponse(
                null,
                [new LlmToolCall("c1", "schedule_create", """{"message":"提醒我喝水","kind":"interval","intervalMinutes":120}""")],
                "tool_calls"),
            new LlmResponse("安排好了", [], "stop"),
        ]);
    }

    private async Task<string> AwaitApprovalAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var queue = await _http.GetFromJsonAsync<JsonElement>("/api/approvals");
            var pending = queue.GetProperty("pending").EnumerateArray().ToList();
            if (pending.Count > 0)
            {
                return pending[0].GetProperty("approvalId").GetString()!;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("no approval was requested for schedule_create");
    }

    private async Task<List<SessionEvent>> WaitForAssistantAsync(string contains)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var events = new List<SessionEvent>();
            await foreach (var logged in _context.Session.ReadAllAsync())
            {
                events.Add(logged);
            }

            if (events.Any(e => e.Kind == SessionEventKind.Assistant && e.Body.Contains(contains, StringComparison.Ordinal)))
            {
                return events;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"assistant never said '{contains}'");
    }

    [Fact]
    public async Task A_task_the_assistant_asks_for_exists_only_after_a_human_approves()
    {
        await _http.PostAsJsonAsync("/api/input", new { source = "Text", payload = "每两小时提醒我喝水" });

        var approval = await AwaitApprovalAsync();

        // Parked, not done: nothing exists while the question is open.
        Assert.Empty(await _context.Scheduler.ListAsync());

        var answered = await _http.PostAsJsonAsync($"/api/approvals/{approval}", new { approved = true });
        answered.EnsureSuccessStatusCode();
        await WaitForAssistantAsync("安排好了");

        var task = Assert.Single(await _context.Scheduler.ListAsync());
        Assert.Equal("提醒我喝水", task.Payload);
        Assert.Equal(TimeSpan.FromMinutes(120), Assert.IsType<IntervalTrigger>(task.Trigger).Interval);
    }

    [Fact]
    public async Task A_refused_request_creates_nothing_and_the_assistant_is_told_so()
    {
        await _http.PostAsJsonAsync("/api/input", new { source = "Text", payload = "每两小时提醒我喝水" });

        var approval = await AwaitApprovalAsync();
        (await _http.PostAsJsonAsync($"/api/approvals/{approval}", new { approved = false })).EnsureSuccessStatusCode();
        var events = await WaitForAssistantAsync("安排好了");

        Assert.Empty(await _context.Scheduler.ListAsync());
        var result = events.Single(e => e.Kind == SessionEventKind.ToolResult);
        Assert.Equal("false", result.Meta?.GetValueOrDefault("ok"));
    }
}
