using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Tools;

/// <summary>
/// The approval channel an irreversible tool call waits in. The important case is the one nobody answers.
/// </summary>
public sealed class QueuedApprovalHandlerTests
{
    [Fact]
    public async Task Nobody_answering_means_denied()
    {
        // A turn that waits forever for a human is worse than a refusal, and an irreversible action must not
        // slip through because the window was closed.
        var handler = new QueuedApprovalHandler(timeout: TimeSpan.FromMilliseconds(60));

        var approved = await handler.RequestAsync("desktop_act", "{}");

        Assert.False(approved);
        Assert.Empty(handler.Pending);
    }

    [Fact]
    public async Task An_answer_settles_the_waiting_call()
    {
        var handler = new QueuedApprovalHandler(timeout: TimeSpan.FromSeconds(30));
        var waiting = handler.RequestAsync("desktop_act", """{"entityId":"1a2b"}""");

        // The queue is what the UI reads, so the request has to be visible while it waits.
        var pending = Assert.Single(handler.Pending);
        Assert.Equal("desktop_act", pending.ToolName);
        Assert.Contains("1a2b", pending.ArgumentsJson, StringComparison.Ordinal);

        Assert.True(handler.Answer(pending.ApprovalId, approved: true));
        Assert.True(await waiting);

        // Settled requests leave the queue: the UI must not keep offering a decision that is already made.
        Assert.Empty(handler.Pending);
    }

    [Fact]
    public async Task A_denial_is_reported_as_a_denial()
    {
        var handler = new QueuedApprovalHandler(timeout: TimeSpan.FromSeconds(30));
        var waiting = handler.RequestAsync("desktop_act", "{}");
        var pending = Assert.Single(handler.Pending);

        Assert.True(handler.Answer(pending.ApprovalId, approved: false));
        Assert.False(await waiting);
    }

    [Fact]
    public async Task Answering_twice_does_not_decide_twice()
    {
        var handler = new QueuedApprovalHandler(timeout: TimeSpan.FromSeconds(30));
        var waiting = handler.RequestAsync("desktop_act", "{}");
        var pending = Assert.Single(handler.Pending);

        Assert.True(handler.Answer(pending.ApprovalId, approved: true));
        Assert.False(handler.Answer(pending.ApprovalId, approved: false));
        Assert.False(handler.Answer("ap-does-not-exist", approved: true));

        Assert.True(await waiting);
    }
}
