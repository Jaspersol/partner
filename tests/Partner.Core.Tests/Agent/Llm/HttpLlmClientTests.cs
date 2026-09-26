using System.Net;
using System.Text;
using System.Text.Json;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Llm;

/// <summary>Captures the outgoing request and answers with a canned body.</summary>
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    public string? RequestBody { get; private set; }

    public string? Authorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        Authorization = request.Headers.Authorization?.ToString();
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// The production model client. What matters here is the wire shape it emits and the shapes it accepts —
/// a wrong field name fails at runtime against a real provider, not at compile time.
/// </summary>
public sealed class HttpLlmClientTests
{
    private static HttpLlmClient Build(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
            ApiKey = "secret",
        });

    private static LlmRequest Request(params LlmMessage[] messages) => new(
        messages,
        new[] { new ToolSpec("clock", "what time is it", "{\"type\":\"object\",\"properties\":{}}") });

    [Fact]
    public async Task It_posts_the_openai_chat_shape_with_the_bearer_token()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hello"},"finish_reason":"stop"}]}""");

        var response = await Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi")));

        Assert.Equal("hello", response.Text);
        Assert.Equal("https://example.test/v1/chat/completions", handler.Request?.RequestUri?.ToString());
        Assert.Equal("Bearer secret", handler.Authorization);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("clock", tool.GetProperty("function").GetProperty("name").GetString());
        // The schema goes back out as JSON, not as an escaped string.
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task It_parses_tool_calls_and_the_finish_reason()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"clock","arguments":"{\"zone\":\"local\"}"}}
            ]},"finish_reason":"tool_calls"}]}
            """);

        var response = await Build(handler).CompleteAsync(Request(new LlmMessage("user", "time?")));

        Assert.Null(response.Text);
        Assert.Equal("tool_calls", response.FinishReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("clock", call.Name);
        Assert.Equal("{\"zone\":\"local\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task It_sends_tool_results_back_with_their_call_id()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""");

        await Build(handler).CompleteAsync(Request(
            new LlmMessage("user", "time?"),
            new LlmMessage("assistant", null, new[] { new LlmToolCall("call_1", "clock", "{}") }),
            new LlmMessage("tool", "12:34", ToolCallId: "call_1", Name: "clock")));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");

        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("call_1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("clock", assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());

        var tool = messages[2];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("12:34", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task It_omits_the_tools_field_when_no_tool_is_exposed()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");

        await Build(handler).CompleteAsync(new LlmRequest(new[] { new LlmMessage("user", "hi") }, Array.Empty<ToolSpec>()));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task A_provider_error_carries_the_status_and_the_body()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi"))));

        Assert.Contains("429", error.Message, StringComparison.Ordinal);
        Assert.Contains("slow down", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_without_choices_is_an_error_not_an_empty_reply()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi"))));
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_swallowed()
    {
        var client = Build(new HangingHandler());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteAsync(Request(new LlmMessage("user", "hi")), cancelled.Token));
    }

    [Fact]
    public void Without_configuration_it_reports_exactly_what_is_missing()
    {
        var previousUrl = Environment.GetEnvironmentVariable(HttpLlmClient.BaseUrlVariable);
        var previousModel = Environment.GetEnvironmentVariable(HttpLlmClient.ModelVariable);

        try
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, null);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, null);

            var (client, reason) = HttpLlmClient.TryFromEnvironment();

            Assert.Null(client);
            Assert.Contains(HttpLlmClient.BaseUrlVariable, reason, StringComparison.Ordinal);
            Assert.Contains(HttpLlmClient.ModelVariable, reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, previousUrl);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, previousModel);
        }
    }

    [Fact]
    public void A_configured_environment_builds_a_client()
    {
        var previousUrl = Environment.GetEnvironmentVariable(HttpLlmClient.BaseUrlVariable);
        var previousModel = Environment.GetEnvironmentVariable(HttpLlmClient.ModelVariable);

        try
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, "http://127.0.0.1:11434/v1/");
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, "local-model");

            var (client, reason) = HttpLlmClient.TryFromEnvironment();

            Assert.NotNull(client);
            // The trailing slash is trimmed so the request path is not doubled.
            Assert.Equal("local-model", client.Model);
            Assert.Equal("已配置模型", reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, previousUrl);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, previousModel);
        }
    }
}
