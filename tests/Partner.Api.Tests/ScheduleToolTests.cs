using Partner.Api.Settings;
using Partner.Api.Agents;
using Partner.Core.Agent.Tools;
using Partner.Core.Scheduler;

namespace Partner.Api.Tests;

/// <summary>
/// The assistant's timed-task tools: what it can set up, what it is told back (the model has no clock), that it is
/// held to the page's rules, and which approval gear asks a human about making or removing a task.
/// </summary>
public sealed class ScheduleToolTests : ApiFixture
{
    protected override string WorkspaceName => "partner-schedule-tool-tests";

    protected override string ReadinessPath => "/api/schedules";

    private ScheduleCreateTool Create => new(_context);

    private ScheduleListTool List => new(_context);

    private ScheduleDeleteTool Delete => new(_context);

    private async Task<IReadOnlyList<ScheduleTask>> TasksAsync() => await _context.Scheduler.ListAsync();

    [Fact]
    public async Task A_cron_task_is_created_and_the_result_says_when_it_runs_and_what_time_it_is()
    {
        var result = await Create.InvokeAsync("""{"message":"总结昨天的待办","kind":"cron","cron":"0 9 * * 1-5"}""");

        Assert.True(result.Ok, result.Content);
        var task = Assert.Single(await TasksAsync());
        Assert.Equal("总结昨天的待办", task.Payload);
        Assert.Equal("0 9 * * 1-5", Assert.IsType<CronTrigger>(task.Trigger).Expression);
        Assert.Contains(task.TaskId, result.Content);
        Assert.Contains("现在是", result.Content);
        Assert.Contains("总结昨天的待办", result.Content);
    }

    [Fact]
    public async Task A_catch_up_policy_given_to_the_tool_is_stored_and_the_default_is_once()
    {
        await Create.InvokeAsync("""{"message":"日报","kind":"cron","cron":"0 9 * * *","catchUp":"skip"}""");
        await Create.InvokeAsync("""{"message":"体检","kind":"cron","cron":"0 8 * * *"}""");

        var tasks = await TasksAsync();
        Assert.Equal(CatchUpPolicy.Skip, tasks.Single(t => t.Payload == "日报").CatchUp);
        Assert.Equal(CatchUpPolicy.Once, tasks.Single(t => t.Payload == "体检").CatchUp);
    }

    [Fact]
    public async Task A_one_off_can_be_given_as_minutes_from_now_even_as_a_string()
    {
        var before = DateTimeOffset.UtcNow;
        var result = await Create.InvokeAsync("""{"message":"站起来","kind":"once","delayMinutes":"90"}""");

        Assert.True(result.Ok, result.Content);
        var task = Assert.Single(await TasksAsync());
        var at = Assert.IsType<OnceTrigger>(task.Trigger).At;
        Assert.InRange(at, before.AddMinutes(90), DateTimeOffset.UtcNow.AddMinutes(90));
    }

    [Fact]
    public async Task A_local_wall_clock_time_is_read_in_the_machines_zone()
    {
        var result = await Create.InvokeAsync("""{"message":"开会","kind":"once","at":"2099-01-01 09:00"}""");

        Assert.True(result.Ok, result.Content);
        var local = new DateTime(2099, 1, 1, 9, 0, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        Assert.Equal(expected, Assert.IsType<OnceTrigger>(Assert.Single(await TasksAsync()).Trigger).At);
    }

    [Fact]
    public async Task A_time_in_the_past_is_refused_and_the_refusal_carries_the_current_time()
    {
        var result = await Create.InvokeAsync("""{"message":"x","kind":"once","at":"2000-01-01 09:00"}""");

        Assert.False(result.Ok);
        Assert.Contains("现在是", result.Content);
        Assert.Empty(await TasksAsync());
    }

    [Theory]
    [InlineData("""{"kind":"cron","cron":"0 9 * * *"}""")]
    [InlineData("""{"message":"x"}""")]
    [InlineData("""{"message":"x","kind":"once"}""")]
    [InlineData("""{"message":"x","kind":"once","delayMinutes":5,"at":"2099-01-01 09:00"}""")]
    [InlineData("""{"message":"x","kind":"once","at":"明天早上"}""")]
    [InlineData("""{"message":"x","kind":"once","delayMinutes":0}""")]
    [InlineData("not json")]
    public async Task Malformed_arguments_are_refused_without_storing_anything(string json)
    {
        var result = await Create.InvokeAsync(json);

        Assert.False(result.Ok);
        Assert.Empty(await TasksAsync());
    }

    [Theory]
    [InlineData("""{"message":"x","kind":"cron","cron":"nope"}""")]
    [InlineData("""{"message":"x","kind":"interval","intervalMinutes":0}""")]
    [InlineData("""{"message":"x","kind":"weekly"}""")]
    public async Task It_is_held_to_the_same_rules_as_the_page(string json)
    {
        var result = await Create.InvokeAsync(json);

        Assert.False(result.Ok);
        Assert.StartsWith("refused:", result.Content);
        Assert.DoesNotContain("Parameter '", result.Content);
        Assert.Empty(await TasksAsync());
    }

    [Fact]
    public async Task Listing_says_what_time_it_is_and_shows_each_task()
    {
        var empty = await List.InvokeAsync("{}");
        Assert.True(empty.Ok);
        Assert.Contains("现在是", empty.Content);
        Assert.Contains("没有定时任务", empty.Content);

        await Create.InvokeAsync("""{"message":"喝水","kind":"interval","intervalMinutes":120}""");
        await Create.InvokeAsync(
            "{\"message\":\"" + new string('长', 100) + "\",\"kind\":\"cron\",\"cron\":\"0 8 * * 1\"}");

        var listed = await List.InvokeAsync("{}");
        Assert.True(listed.Ok);
        Assert.Contains("共 2 个", listed.Content);
        Assert.Contains("每 2 小时", listed.Content);
        Assert.Contains("cron 0 8 * * 1", listed.Content);
        Assert.Contains("喝水", listed.Content);

        // A long message is cut, so listing a hundred tasks does not flood the model's context.
        Assert.DoesNotContain(new string('长', 100), listed.Content);
        Assert.Contains("…", listed.Content);
    }

    [Fact]
    public async Task Deleting_removes_the_task_and_an_unknown_id_is_not_found()
    {
        await Create.InvokeAsync("""{"message":"x","kind":"interval","intervalMinutes":60}""");
        var id = Assert.Single(await TasksAsync()).TaskId;

        var gone = await Delete.InvokeAsync($$"""{"id":"{{id}}"}""");
        Assert.True(gone.Ok, gone.Content);
        Assert.Empty(await TasksAsync());

        var again = await Delete.InvokeAsync($$"""{"id":"{{id}}"}""");
        Assert.False(again.Ok);
        Assert.StartsWith("not_found", again.Content);

        Assert.False((await Delete.InvokeAsync("{}")).Ok);
    }

    [Fact]
    public void Reading_is_allowed_outright_but_creating_and_deleting_follow_the_gear()
    {
        // Creating a task is put away for later and can be deleted again; deleting one is what cannot be undone.
        // So the middle gear asks for the deletion only, and the loosest gear asks for neither.
        var governance = new DesktopGovernance(_context);
        Assert.Equal(ToolDecision.Allow, governance.Decide("schedule_list", "{}").Decision);

        Assert.Equal(ToolDecision.Ask, governance.Decide("schedule_create", "{}").Decision);
        Assert.Equal(ToolDecision.Ask, governance.Decide("schedule_delete", "{}").Decision);

        try
        {
            SaveGear(ApprovalGears.Dangerous);
            Assert.Equal(ToolDecision.Allow, governance.Decide("schedule_create", "{}").Decision);
            Assert.Equal(ToolDecision.Ask, governance.Decide("schedule_delete", "{}").Decision);

            SaveGear(ApprovalGears.None);
            Assert.Equal(ToolDecision.Allow, governance.Decide("schedule_create", "{}").Decision);
            Assert.Equal(ToolDecision.Allow, governance.Decide("schedule_delete", "{}").Decision);

            // Reading stays the same in every gear: the gear is about acting.
            Assert.Equal(ToolDecision.Allow, governance.Decide("schedule_list", "{}").Decision);
        }
        finally
        {
            SaveGear(ApprovalGears.All);
        }
    }

    private void SaveGear(string gear)
    {
        var settings = _context.Settings.Load();
        _context.Settings.Save(settings with { Desktop = settings.Desktop with { Approval = gear } });
    }
}
