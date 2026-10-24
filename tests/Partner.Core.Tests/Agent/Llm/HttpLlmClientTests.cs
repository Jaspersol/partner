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

/// <summary>Captures the outgoing request and answers with a canned body and headers.</summary>
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public int Requests { get; private set; }

    public HttpRequestMessage? Request { get; private set; }

    public string? RequestBody { get; private set; }

    public string? Authorization { get; private set; }

    /// <summary>
    /// Headers set on the answer, e.g. the id a provider wants quoted in a support request. On the instance
    /// rather than on a fresh message, so a test can add one before the call it is asserting about.
    /// </summary>
    public System.Net.Http.Headers.HttpResponseHeaders ResponseHeaders { get; } = new HttpResponseMessage().Headers;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        Request = request;
        Authorization = request.Headers.Authorization?.ToString();
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        foreach (var header in ResponseHeaders)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
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
        var handler = new SseHandler(
            Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"clock","arguments":"{\"city\":\""}}]}}]}"""),
            Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"上海\"}"}}]}}]}"""),
            "data: [DONE]");

        var response = await Build(handler).StreamAsync(Request(new LlmMessage("user", "hi")), _ => { });

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("clock", call.Name);
        Assert.Equal("{\"city\":\"上海\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task A_server_that_refuses_streaming_falls_back_to_the_buffered_call()
    {
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

    [Fact]
    public async Task Streaming_keeps_the_reasoning_channel_apart_from_the_answer()
    {
        // The two arrive interleaved and on separate keys depending on the vendor, and before any answer text.
        var handler = new SseHandler(
            Frame("""{"choices":[{"delta":{"reasoning_content":"先想 "}}]}"""),
            Frame("""{"choices":[{"delta":{"reasoning_content":"一下"}}]}"""),
            Frame("""{"choices":[{"delta":{"content":"答"}}]}"""),
            "data: [DONE]");

        var text = new List<string>();
        var reasoning = new List<string>();
        var response = await Build(handler)
            .StreamWithReasoningAsync(Request(new LlmMessage("user", "hi")), text.Add, reasoning.Add);

        Assert.Equal(new[] { "先想 ", "一下" }, reasoning);
        Assert.Equal(new[] { "答" }, text);
        Assert.Equal("先想 一下", response.Reasoning);
        Assert.Equal("答", response.Text);
    }

    [Fact]
    public async Task Streaming_reads_the_reasoning_key_a_vendor_happens_to_use()
    {
        // OpenAI's reasoning models answer under `reasoning` rather than `reasoning_content`; both are read.
        var handler = new SseHandler(
            Frame("""{"choices":[{"delta":{"reasoning":"pondering"}}]}"""),
            "data: [DONE]");

        var response = await Build(handler)
            .StreamWithReasoningAsync(Request(new LlmMessage("user", "hi")), _ => { }, _ => { });

        Assert.Equal("pondering", response.Reasoning);
    }

    [Fact]
    public async Task A_model_that_does_not_think_reports_no_reasoning_at_all()
    {
        // Null, not an empty string: "the model did not think" and "the endpoint sent nothing" are one answer,
        // and a view must not draw them as two different things.
        var handler = new SseHandler(
            Frame("""{"choices":[{"delta":{"content":"答"}}]}"""),
            "data: [DONE]");

        var response = await Build(handler)
            .StreamWithReasoningAsync(Request(new LlmMessage("user", "hi")), _ => { }, _ => { });

        Assert.Null(response.Reasoning);
    }

    [Fact]
    public async Task A_buffered_reply_keeps_its_reasoning_too()
    {
        // A server that refuses `stream: true` loses the live view but must not lose the content.
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"答","reasoning_content":"想过了"},"finish_reason":"stop"}]}""");

        var response = await Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi")));

        Assert.Equal("想过了", response.Reasoning);
        Assert.Equal("答", response.Text);
    }

    [Fact]
    public async Task A_client_that_only_knows_the_buffered_call_still_works()
    {
        // The reasoning callback is a default method: clients that only know the buffered call still get the
        // reasoning, just not while it happens.
        ILlmClient client = new ScriptedLlmClient(
            new[] { new LlmResponse("答", Array.Empty<LlmToolCall>(), "stop", Reasoning: "想过了") });

        var reasoning = new List<string>();
        var response = await client.StreamWithReasoningAsync(
            Request(new LlmMessage("user", "hi")), _ => { }, reasoning.Add);

        Assert.Equal(new[] { "想过了" }, reasoning);
        Assert.Equal("想过了", response.Reasoning);
    }

    [Fact]
    public async Task A_rejection_says_what_to_do_and_keeps_the_id_to_quote()
    {
        // The three facts a failure has to carry: what it was, why, and what to do; the id is what the
        // provider's support will ask for.
        var handler = new StubHandler(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"field 'model' is required"}}""");
        handler.ResponseHeaders.Add("x-request-id", "req_abc123");

        var failure = await Assert.ThrowsAsync<ModelCallFailure>(
            () => Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi"))));

        Assert.Equal(ModelCallFailure.FailureKind.Rejected, failure.Kind);
        Assert.Equal("模型补全", failure.What);
        Assert.Equal("HTTP 400", failure.Value);
        Assert.Equal("req_abc123", failure.RequestId);
        Assert.Contains("reasoning_effort", failure.NextStep, StringComparison.Ordinal);

        // The message is the shape everything else can rely on: <what>: <value> (<reason>; <next>).
        Assert.StartsWith("模型补全: HTTP 400 (", failure.Message, StringComparison.Ordinal);

        // And the provider's own words survive, because the summary is a summary and not a replacement.
        Assert.Contains("field 'model' is required", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_body_that_echoes_a_key_is_cleaned_before_it_is_kept()
    {
        // An error body can repeat the credential back, and the session log is meant to be readable.
        var handler = new StubHandler(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"bad key sk-abcdef123456 for model"}}""");

        var failure = await Assert.ThrowsAsync<ModelCallFailure>(
            () => Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi"))));

        Assert.DoesNotContain("sk-abcdef123456", failure.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdef123456", failure.Message, StringComparison.Ordinal);
        Assert.Contains("bad key", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_points_at_the_credential_rather_than_saying_bad_request()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "nope");

        var failure = await Assert.ThrowsAsync<ModelCallFailure>(
            () => Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi"))));

        Assert.Contains("凭据", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("API Key", failure.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dead_endpoint_says_it_could_not_be_reached_and_names_the_next_step()
    {
        var failure = ModelCallFailure.From(
            "模型补全",
            new HttpRequestException("refused", new System.Net.Sockets.SocketException(10061)));

        Assert.Equal(ModelCallFailure.FailureKind.Unreachable, failure.Kind);
        Assert.Contains("确认端点在运行", failure.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancellation_reports_as_a_timeout_naming_the_limit_it_waited()
    {
        // The transport cannot tell a cancellation apart from a deadline, so the timeout must name the limit
        // the reader is about to go change.
        var failure = ModelCallFailure.From("模型补全", new TaskCanceledException(), TimeSpan.FromSeconds(120));

        Assert.Equal(ModelCallFailure.FailureKind.Timeout, failure.Kind);
        Assert.Equal("120s", failure.Value);
        Assert.Contains("超时", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_with_no_known_limit_says_so_rather_than_saying_zero()
    {
        // A caller that genuinely does not know the limit must not print a number that reads as an instant.
        var failure = ModelCallFailure.TimedOut("模型补全", limit: null);

        Assert.DoesNotContain("0s", failure.Message, StringComparison.Ordinal);
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
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_default_thinking_gear_is_medium()
    {
        // The gear is a setting with a default, not an opt-in.
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}""");

        await Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi")));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("medium", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task The_gear_off_leaves_the_field_out_entirely()
    {
        // A model that does not take the parameter must not be sent a guess at one: off is the absence of the
        // field, not a value the server has to recognise.
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}""");

        var client = new HttpLlmClient(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
            ReasoningEffort = ReasoningEfforts.Off,
        });

        await client.CompleteAsync(Request(new LlmMessage("user", "hi")));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task A_gear_the_menu_does_not_offer_is_passed_through()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}""");

        var client = new HttpLlmClient(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
            ReasoningEffort = "  MINIMAL ",
        });

        await client.CompleteAsync(Request(new LlmMessage("user", "hi")));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("minimal", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_gear_on_the_request_overrides_the_configured_default()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}""");

        var client = new HttpLlmClient(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
            ReasoningEffort = "medium",
        });

        await client.CompleteAsync(
            Request(new LlmMessage("user", "hi")) with { ReasoningEffort = "high" });

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_request_without_a_gear_falls_back_to_the_configured_default()
    {
        // Null means "no choice was made", which is not the same as off: a voice or scheduled turn carries
        // no gear and must still get the default rather than silently losing the parameter.
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}""");

        var client = new HttpLlmClient(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
            ReasoningEffort = "high",
        });

        await client.CompleteAsync(Request(new LlmMessage("user", "hi")) with { ReasoningEffort = null });

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_request_can_turn_the_gear_off_for_one_turn()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}""");

        var client = new HttpLlmClient(new HttpClient(handler), new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "test-model",
            ReasoningEffort = "high",
        });

        await client.CompleteAsync(
            Request(new LlmMessage("user", "hi")) with { ReasoningEffort = ReasoningEfforts.Off });

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void The_gear_is_normalized_but_never_clamped()
    {
        Assert.Equal("high", ReasoningEfforts.Normalize("  HIGH "));
        Assert.Equal("xhigh", ReasoningEfforts.Wire("xhigh"));
        Assert.Equal("none", ReasoningEfforts.Wire("none"));
        Assert.Null(ReasoningEfforts.Wire(ReasoningEfforts.Off));
        Assert.Null(ReasoningEfforts.Wire("   "));
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
    public async Task A_provider_error_carries_the_status_the_body_and_a_next_step()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""");

        var error = await Assert.ThrowsAsync<ModelCallFailure>(
            () => Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi"))));

        Assert.Contains("429", error.Message, StringComparison.Ordinal);
        Assert.Contains("slow down", error.Message, StringComparison.Ordinal);
        Assert.Contains("限流", error.Reason, StringComparison.Ordinal);
        Assert.Contains("等一会儿", error.NextStep, StringComparison.Ordinal);
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
    public void The_environment_can_select_the_protocol_and_rejects_an_unknown_one()
    {
        var previousUrl = Environment.GetEnvironmentVariable(HttpLlmClient.BaseUrlVariable);
        var previousModel = Environment.GetEnvironmentVariable(HttpLlmClient.ModelVariable);
        var previousProtocol = Environment.GetEnvironmentVariable(HttpLlmClient.ProtocolVariable);

        try
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, "https://api.anthropic.com/v1");
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, "claude-test");
            Environment.SetEnvironmentVariable(HttpLlmClient.ProtocolVariable, "claude");

            var (options, _) = HttpLlmClient.OptionsFromEnvironment();
            Assert.Equal(LlmProtocols.Anthropic, options?.Protocol);

            Environment.SetEnvironmentVariable(HttpLlmClient.ProtocolVariable, "gemini");
            var (invalid, reason) = HttpLlmClient.OptionsFromEnvironment();

            Assert.Null(invalid);
            Assert.Contains(HttpLlmClient.ProtocolVariable, reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, previousUrl);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, previousModel);
            Environment.SetEnvironmentVariable(HttpLlmClient.ProtocolVariable, previousProtocol);
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

    [Fact]
    public async Task The_system_message_is_marked_cacheable_only_when_asked()
    {
        const string Reply = """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""";
        var messages = new[] { new LlmMessage("system", "你是助手。"), new LlmMessage("user", "hi") };

        var plain = new StubHandler(HttpStatusCode.OK, Reply);
        await new HttpLlmClient(new HttpClient(plain), new HttpLlmOptions { BaseUrl = "https://example.test/v1", Model = "m" })
            .CompleteAsync(Request(messages));
        using (var body = JsonDocument.Parse(plain.RequestBody!))
        {
            // Untouched: the system message is the plain string every server understands.
            Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind);
        }

        var marked = new StubHandler(HttpStatusCode.OK, Reply);
        await new HttpLlmClient(
                new HttpClient(marked),
                new HttpLlmOptions { BaseUrl = "https://example.test/v1", Model = "m", PromptCacheMarkers = true })
            .CompleteAsync(Request(messages));
        using var markedBody = JsonDocument.Parse(marked.RequestBody!);
        var system = markedBody.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Equal("你是助手。", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, markedBody.RootElement.GetProperty("messages")[1].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task The_servers_prompt_token_count_is_carried_on_the_response()
    {
        var buffered = new StubHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1234,"completion_tokens":7}}""");
        var response = await Build(buffered).CompleteAsync(Request(new LlmMessage("user", "hi")));
        Assert.Equal(1234, response.PromptTokens);
        Assert.Equal(7, response.CompletionTokens);

        var streamed = new SseHandler(
            Frame("""{"choices":[{"delta":{"content":"你好"}}]}"""),
            Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}"""),
            Frame("""{"choices":[],"usage":{"prompt_tokens":4321,"completion_tokens":9}}"""),
            "data: [DONE]");
        var final = await Build(streamed).StreamAsync(Request(new LlmMessage("user", "hi")), _ => { });
        Assert.Equal(4321, final.PromptTokens);
        Assert.Equal("你好", final.Text);
    }

    [Fact]
    public async Task A_server_that_reports_no_usage_leaves_the_count_at_zero()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");

        Assert.Equal(0, (await Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi")))).PromptTokens);
    }
}
