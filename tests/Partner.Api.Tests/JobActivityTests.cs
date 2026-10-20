using Partner.Api.Agents;
using Partner.Core.Agent.Tools;

namespace Partner.Api.Tests;

/// <summary>While a sub-agent works, its job says which tool it is inside.</summary>
public sealed class JobActivityTests
{
    private sealed class SlowTool : ITimedTool
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolSpec Spec { get; } = new("web_fetch", "fetch", "{\"type\":\"object\"}");

        public TimeSpan Timeout => TimeSpan.FromSeconds(45);

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new ToolResult(true, "done");
        }
    }

    private static AgentJob NewJob() => new("job-1", "agent", "联网搜索", "web-search", "查一下", 3, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task The_job_shows_the_tool_it_is_inside_and_clears_it_afterwards()
    {
        var job = NewJob();
        var inner = new InMemoryToolRegistry();
        var tool = new SlowTool();
        inner.Register(tool);
        var registry = new ObservedToolRegistry(inner, job.SetActivity);

        Assert.Null(job.View().Activity);

        var running = registry.Find("web_fetch")!.InvokeAsync("{}");
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("web_fetch", job.View().Activity);

        tool.Release.SetResult();
        await running;
        Assert.Null(job.View().Activity);
    }

    [Fact]
    public void The_wrapper_keeps_the_tools_own_time_budget_and_specs()
    {
        var inner = new InMemoryToolRegistry();
        inner.Register(new SlowTool());
        var registry = new ObservedToolRegistry(inner, _ => { });

        Assert.Equal("web_fetch", Assert.Single(registry.Specs).Name);
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.IsAssignableFrom<ITimedTool>(registry.Find("web_fetch")).Timeout);
        Assert.Null(registry.Find("nope"));
    }

    [Fact]
    public void A_finished_job_has_no_activity()
    {
        var job = NewJob();
        job.SetActivity("web_search");
        job.Finish(
            new Partner.Core.Agent.Delegation.JobOutcome(true, null, "ok", "e", null, 1, 3, [], []),
            DateTimeOffset.UnixEpoch);

        Assert.Null(job.View().Activity);
    }

    [Fact]
    public void Attachment_paths_are_read_from_the_arguments_and_bounded()
    {
        var many = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"C:/docs/f{i}.txt\""));

        Assert.Equal(
            new[] { "C:/docs/a.txt", "C:/docs/b.txt" },
            Delegation.Strings("""{"attachments":["C:/docs/a.txt"," C:/docs/b.txt ","",5,null]}""", "attachments"));
        Assert.Empty(Delegation.Strings("""{"goal":"x"}""", "attachments"));
        Assert.Empty(Delegation.Strings("""{"attachments":"not an array"}""", "attachments"));
        Assert.Empty(Delegation.Strings("not json", "attachments"));
        Assert.Equal(10, Delegation.Strings($$"""{"attachments":[{{many}}]}""", "attachments").Count);
    }
}
