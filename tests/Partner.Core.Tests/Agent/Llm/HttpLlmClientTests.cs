using System.Net;
using System.Text;
using System.Text.Json;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Llm;

/// <summary>Answers each request from a prepared queue, in order.</summary>
internal sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        return Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}

/// <summary>
/// Answers with an SSE stream. Frames are released one at a time, so a caller can be observed while the reply
/// is still arriving — which is the only way to test that streaming is actually streaming.
/// </summary>
internal sealed class SseHandler(params string[] frames) : HttpMessageHandler
{
    private readonly IReadOnlyList<string> _frames = frames;

    public int Requests { get; private set; }

    public string? RequestBody { get; private set; }

    /// <summary>When set, the stream waits for it before releasing the next frame.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Released { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FrameStream(this, ct)),
        };
    }

    private sealed class FrameStream(SseHandler owner, CancellationToken ct) : Stream
    {
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (owner.Released >= owner._frames.Count)
                {
                    return 0;
                }

                if (owner.Gate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    owner.Gate = null;
                }

                _current = Encoding.UTF8.GetBytes(owner._frames[owner.Released] + "\n\n");
                _offset = 0;
                owner.Released++;
            }

            var take = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Captures the outgoing request and answers with a canned body.</summary>
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public int Requests { get; private set; }

    public HttpRequestMessage? Request { get; private set; }

    public string? RequestBody { get; private set; }

    public string? Authorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
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

    private static string Frame(string json) => "data: " + json;

    [Fact]
    public async Task A_client_without_streaming_reports_its_answer_in_one_piece()
    {
        // This is what makes StreamAsync an addition rather than a break: an implementation that only knows
        // CompleteAsync still satisfies the interface.
        ILlmClient client = new ScriptedLlmClient(
            new[] { new LlmResponse("all at once", Array.Empty<LlmToolCall>(), "stop") });

        var deltas = new List<string>();
        var response = await client.StreamAsync(Request(new LlmMessage("user", "hi")), deltas.Add);

        Assert.Equal(new[] { "all at once" }, deltas);
        Assert.Equal("all at once", response.Text);
    }

    [Fact]
    public async Task Streaming_reports_text_as_it_arrives_and_assembles_the_response()
    {
        var handler = new SseHandler(
            Frame("{\"choices\":[{\"delta\":{\"content\":\"你\"}}]}"),
            Frame("{\"choices\":[{\"delta\":{\"content\":\"好\"}}]}"),
            Frame("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"),
            "data: [DONE]");

        var deltas = new List<string>();
        var response = await Build(handler).StreamAsync(Request(new LlmMessage("user", "hi")), deltas.Add);

        // The caller sees fragments, the return value is the whole thing — and only the return value is logged.
        Assert.Equal(new[] { "你", "好" }, deltas);
        Assert.Equal("你好", response.Text);
        Assert.Equal("stop", response.FinishReason);
        Assert.Empty(response.ToolCalls);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Streaming_assembles_tool_calls_from_fragments()
    {
        // Raw string literals: the JSON itself contains escaped quotes, and a raw literal passes them through.
        var handler = new SseHandler(
            Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"clock","arguments":"{\"city\":\""}}]}}]}"""),
            Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"上海\"}"}}]}}]}"""),
            "data: [DONE]");

        var response = await Build(handler).StreamAsync(Request(new LlmMessage("user", "hi")), _ => { });

        // A tool call arrives split: the name in one frame, the JSON arguments across the rest.
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("clock", call.Name);
        Assert.Equal("{\"city\":\"上海\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task A_server_that_refuses_streaming_falls_back_to_the_buffered_call()
    {
        // Some local servers only implement one of the two shapes. Failing the turn over that would be silly.
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("stream not supported") },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"buffered\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            });

        var deltas = new List<string>();
        var response = await Build(handler).StreamAsync(Request(new LlmMessage("user", "hi")), deltas.Add);

        Assert.Equal("buffered", response.Text);
        Assert.Equal(new[] { "buffered" }, deltas);
        Assert.Equal(2, handler.Requests);
    }

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
            Assert.Contains("环境变量", reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, previousUrl);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, previousModel);
        }
    }
}
