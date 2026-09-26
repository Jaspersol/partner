using System.Net;
using Partner.Core.Agent.Llm;

namespace Partner.Core.Tests.Agent.Llm;

/// <summary>
/// Model-native token counting. A provider count is authoritative for its own model, but it is a network call
/// — so it is memoised, and a server without the endpoint must not be asked again on every turn.
/// </summary>
public sealed class HttpLlmTokenCounterTests
{
    private static HttpLlmClient Build(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
        });

    [Fact]
    public async Task A_canceled_turn_is_not_answered_from_the_estimate()
    {
        // A timeout and a cancellation are the same exception type; only the caller's token tells them apart,
        // and swallowing a cancellation would let a stopped turn keep working.
        using var cts = new CancellationTokenSource();
        var pending = Build(new HangingHandler()).CountAsync("你好", cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task It_counts_with_the_tokenizer_endpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tokens":[1,2,3,4,5]}""");

        var count = await Build(handler).CountAsync("你好，世界");

        Assert.Equal(5, count);
        Assert.Contains("/tokenize", handler.Request?.RequestUri?.ToString(), StringComparison.Ordinal);
        // Raw UTF-8, not \uXXXX: escaping would inflate every CJK request.
        Assert.Contains("\"content\":\"你好，世界\"", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_synchronous_path_answers_from_the_local_estimate()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tokens":[1,2,3,4,5]}""");
        var client = Build(handler);

        // 12 characters / 4: the sync path cannot do I/O, so unknown text falls back to the local estimate.
        Assert.Equal(3, client.Count("abcdefghijkl"));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task The_synchronous_path_answers_from_what_was_already_counted()
    {
        // The session view reports occupancy on every load. It must answer from the memo the turns filled, not
        // from a fresh round trip — a dead endpoint would otherwise hang the view.
        var handler = new StubHandler(HttpStatusCode.OK, """{"tokens":[1,2,3,4,5,6,7]}""");
        var client = Build(handler);

        Assert.Equal(7, await client.CountAsync("你好，世界"));
        Assert.Equal(1, handler.Requests);

        // Known text: answered from memory, with no second request — and the model's number, not the estimate.
        Assert.Equal(7, client.Count("你好，世界"));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_repeat_is_answered_from_the_cache()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tokens":[1,2,3]}""");
        var client = Build(handler);

        Assert.Equal(3, await client.CountAsync("same text"));
        Assert.Equal(3, await client.CountAsync("same text"));
        Assert.Equal(3, await client.CountAsync("same text"));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_server_without_the_endpoint_is_not_asked_again()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"error":"unknown endpoint"}""");
        var client = Build(handler);

        var first = await client.CountAsync("abcdefghijkl");
        var second = await client.CountAsync("another body");

        // Falls back to the local estimate, and stops asking: a missing endpoint is a permanent answer.
        Assert.Equal(3, first);
        Assert.Equal(3, second);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_transient_failure_falls_back_without_disabling_the_endpoint()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        var client = Build(handler);

        Assert.Equal(3, await client.CountAsync("abcdefghijkl"));
        Assert.Equal(3, await client.CountAsync("abcdefghijkl"));

        // Still asked the second time: one 500 should not turn native counting off for the session.
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Empty_text_costs_nothing_and_costs_no_request()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tokens":[1,2,3]}""");
        var client = Build(handler);

        Assert.Equal(0, await client.CountAsync(string.Empty));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task A_response_without_tokens_falls_back_rather_than_reporting_zero()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"unexpected":true}""");

        // Zero would make every budget appear to have unlimited room.
        Assert.Equal(3, await Build(handler).CountAsync("abcdefghijkl"));
    }
}
