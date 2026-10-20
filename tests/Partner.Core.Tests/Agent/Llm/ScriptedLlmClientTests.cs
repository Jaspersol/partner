using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Llm;

public sealed class ScriptedLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_ReplaysResponsesInOrderAndRecordsRequests()
    {
        var first = new LlmResponse("first answer", Array.Empty<LlmToolCall>(), "stop");
        var second = new LlmResponse(
            null,
            new[] { new LlmToolCall("call-1", "echo", "{\"text\":\"hi\"}") },
            "tool_calls");
        var client = new ScriptedLlmClient(new[] { first, second });

        var tools = new[] { new ToolSpec("echo", "回显输入", "{\"type\":\"object\"}") };
        var messages = new List<LlmMessage>
        {
            new("system", "you are a scripted test"),
            new("user", "hello"),
        };

        var r1 = await client.CompleteAsync(new LlmRequest(messages, tools));
        var r2 = await client.CompleteAsync(new LlmRequest(new[] { new LlmMessage("user", "again") }, tools));

        Assert.Same(first, r1);
        Assert.Same(second, r2);
        Assert.Equal("first answer", r1.Text);
        Assert.Equal("stop", r1.FinishReason);
        Assert.Null(r2.Text);
        Assert.Equal("tool_calls", r2.FinishReason);
        Assert.Equal("call-1", Assert.Single(r2.ToolCalls).Id);
        Assert.Equal("{\"text\":\"hi\"}", r2.ToolCalls[0].ArgumentsJson);

        // Every request is recorded, including role, content and tool list.
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(new[] { "system", "user" }, client.Requests[0].Messages.Select(m => m.Role));
        Assert.Equal("you are a scripted test", client.Requests[0].Messages[0].Content);
        Assert.Equal("hello", client.Requests[0].Messages[1].Content);
        Assert.Equal("echo", Assert.Single(client.Requests[0].Tools).Name);
        Assert.Equal("again", Assert.Single(client.Requests[1].Messages).Content);
    }

    [Fact]
    public async Task CompleteAsync_SnapshotsMessagesSoLaterMutationDoesNotRewriteHistory()
    {
        var client = new ScriptedLlmClient(new[] { new LlmResponse("ok", Array.Empty<LlmToolCall>(), "stop") });
        var messages = new List<LlmMessage> { new("user", "original") };

        await client.CompleteAsync(new LlmRequest(messages, Array.Empty<ToolSpec>()));

        messages.Add(new LlmMessage("user", "injected afterwards"));
        messages[0] = new LlmMessage("user", "replaced afterwards");

        var recorded = Assert.Single(client.Requests).Messages;
        Assert.Equal("original", Assert.Single(recorded).Content);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsWhenScriptIsExhausted()
    {
        var client = new ScriptedLlmClient(Array.Empty<LlmResponse>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(0, client.Remaining);

        // Calls after the script is exhausted still fail; no response is invented.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task CompleteAsync_PreCancelledTokenThrowsWithoutConsumingTheScript()
    {
        var client = new ScriptedLlmClient(new[] { new LlmResponse("ok", Array.Empty<LlmToolCall>(), "stop") });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(Request(), cts.Token));

        // The queue is untouched, so the next normal call still gets the same scripted response.
        Assert.Equal(1, client.Remaining);
        Assert.Equal("ok", (await client.CompleteAsync(Request())).Text);
    }

    [Fact]
    public async Task Remaining_CountsDownAsResponsesAreConsumed()
    {
        var client = new ScriptedLlmClient(new[]
        {
            new LlmResponse("a", Array.Empty<LlmToolCall>(), "stop"),
            new LlmResponse("b", Array.Empty<LlmToolCall>(), "stop"),
            new LlmResponse("c", Array.Empty<LlmToolCall>(), "stop"),
        });

        Assert.Equal(3, client.Remaining);
        Assert.Equal("a", (await client.CompleteAsync(Request())).Text);
        Assert.Equal(2, client.Remaining);
        Assert.Equal("b", (await client.CompleteAsync(Request())).Text);
        Assert.Equal(1, client.Remaining);
        Assert.Equal("c", (await client.CompleteAsync(Request())).Text);
        Assert.Equal(0, client.Remaining);
    }

    private static LlmRequest Request() => new(
        new[] { new LlmMessage("user", "hi") },
        Array.Empty<ToolSpec>());
}
