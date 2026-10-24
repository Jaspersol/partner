using System.Net;
using System.Text.Json;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent.Llm;

/// <summary>
/// The Anthropic Messages wire: a wrong field name fails against a real provider, not at compile time, so the
/// request shape and the stream events are asserted literally.
/// </summary>
public sealed class AnthropicLlmClientTests
{
    private static AnthropicLlmClient Build(HttpMessageHandler handler, HttpLlmOptions? options = null) =>
        new(new HttpClient(handler), options ?? new HttpLlmOptions
        {
            BaseUrl = "https://api.example.test/v1",
            Model = "claude-test",
            ApiKey = "secret",
            Protocol = LlmProtocols.Anthropic,
            MaxOutputTokens = 2048,
        });

    private static LlmRequest Request(params LlmMessage[] messages) => new(
        messages,
        new[] { new ToolSpec("clock", "what time is it", "{\"type\":\"object\",\"properties\":{}}") });

    private static string Frame(string json) => "data: " + json;

    [Fact]
    public async Task It_posts_the_anthropic_messages_shape_with_the_api_key_header()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"type":"message","role":"assistant","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":9,"output_tokens":3,"cache_creation_input_tokens":2,"cache_read_input_tokens":5}}""");

        var response = await Build(handler).CompleteAsync(Request(
            new LlmMessage("system", "你是助手。"),
            new LlmMessage("user", "hi")));

        Assert.Equal("hello", response.Text);
        Assert.Equal("stop", response.FinishReason);
        // Cached input is still input the model read: 9 uncached + 2 written + 5 read.
        Assert.Equal(16, response.PromptTokens);
        Assert.Equal(3, response.CompletionTokens);

        Assert.Equal("https://api.example.test/v1/messages", handler.Request?.RequestUri?.ToString());
        Assert.Equal("secret", Assert.Single(handler.Request!.Headers.GetValues("x-api-key")));
        Assert.Equal(AnthropicLlmClient.ApiVersion, Assert.Single(handler.Request.Headers.GetValues("anthropic-version")));
        Assert.Null(handler.Request.Headers.Authorization);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("claude-test", body.RootElement.GetProperty("model").GetString());
        // Anthropic requires max_tokens on every request; the configured ceiling supplies it.
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("你是助手。", body.RootElement.GetProperty("system").GetString());

        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content")[0].GetProperty("text").GetString());

        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("clock", tool.GetProperty("name").GetString());
        Assert.Equal("what time is it", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("function", out _));
    }

    [Fact]
    public async Task Tool_calls_and_results_use_tool_use_and_tool_result_blocks()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """
            {"type":"message","role":"assistant","content":[
              {"type":"tool_use","id":"toolu_1","name":"clock","input":{"zone":"local"}}
            ],"stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":5}}
            """);

        var response = await Build(handler).CompleteAsync(Request(
            new LlmMessage("user", "time?"),
            new LlmMessage("assistant", null, new[] { new LlmToolCall("toolu_1", "clock", "{\"zone\":\"local\"}") }),
            new LlmMessage("tool", "12:34", ToolCallId: "toolu_1", Name: "clock")));

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("clock", call.Name);
        Assert.Equal("tool_calls", response.FinishReason);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");

        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var toolUse = assistant.GetProperty("content")[0];
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolUse.GetProperty("id").GetString());
        Assert.Equal("local", toolUse.GetProperty("input").GetProperty("zone").GetString());

        // A tool result is a user turn on this wire, not a `tool` role.
        var toolResult = messages[2];
        Assert.Equal("user", toolResult.GetProperty("role").GetString());
        Assert.Equal("tool_result", toolResult.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolResult.GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("12:34", toolResult.GetProperty("content")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Consecutive_user_turns_are_merged_into_one()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"type":"message","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""");

        await Build(handler).CompleteAsync(Request(
            new LlmMessage("assistant", null, new[] { new LlmToolCall("toolu_1", "clock", "{}") }),
            new LlmMessage("tool", "image follows", ToolCallId: "toolu_1"),
            new LlmMessage("user", "here it is", Images: new[] { "data:image/png;base64,AAAA" })));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("assistant", messages[0].GetProperty("role").GetString());
        var user = messages[1].GetProperty("content");
        Assert.Equal(3, user.GetArrayLength());
        Assert.Equal("tool_result", user[0].GetProperty("type").GetString());
        Assert.Equal("text", user[1].GetProperty("type").GetString());
        Assert.Equal("image", user[2].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Attached_images_become_base64_source_blocks()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"type":"message","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""");

        await Build(handler).CompleteAsync(Request(new LlmMessage(
            "user",
            "look",
            Images: new[] { "data:image/jpeg;base64,/9j/4AAQ" })));

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal("text", content[0].GetProperty("type").GetString());
        var image = content[1];
        Assert.Equal("image", image.GetProperty("type").GetString());
        var source = image.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Equal("/9j/4AAQ", source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Streaming_assembles_text_tool_calls_and_usage()
    {
        var handler = new SseHandler(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"好\"}}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"clock\",\"input\":{}}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"上海\\\"}\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":42}}",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}");

        var deltas = new List<string>();
        var response = await Build(handler).StreamAsync(
            Request(new LlmMessage("user", "hi")),
            deltas.Add);

        Assert.Equal(new[] { "你", "好" }, deltas);
        Assert.Equal("你好", response.Text);
        Assert.Equal("tool_calls", response.FinishReason);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("clock", call.Name);
        Assert.Equal("{\"city\":\"上海\"}", call.ArgumentsJson);

        Assert.Equal(12, response.PromptTokens);
        Assert.Equal(42, response.CompletionTokens);
    }

    [Fact]
    public async Task Streaming_keeps_thinking_out_of_the_answer()
    {
        var handler = new SseHandler(
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"先想\"}}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"答\"}}",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}");

        var text = new List<string>();
        var reasoning = new List<string>();
        var response = await Build(handler).StreamWithReasoningAsync(
            Request(new LlmMessage("user", "hi")),
            text.Add,
            reasoning.Add);

        Assert.Equal(new[] { "先想" }, reasoning);
        Assert.Equal(new[] { "答" }, text);
        Assert.Equal("先想", response.Reasoning);
        Assert.Equal("答", response.Text);
    }

    [Fact]
    public async Task A_buffered_reply_keeps_its_thinking_blocks()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """
            {"type":"message","content":[
              {"type":"thinking","thinking":"想一想"},
              {"type":"text","text":"答"}
            ],"stop_reason":"end_turn"}
            """);

        var response = await Build(handler).CompleteAsync(Request(new LlmMessage("user", "hi")));

        Assert.Equal("想一想", response.Reasoning);
        Assert.Equal("答", response.Text);
    }

    [Fact]
    public async Task Prompt_cache_marks_the_system_block_only_when_asked()
    {
        const string Reply = """{"type":"message","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""";

        var marked = new StubHandler(HttpStatusCode.OK, Reply);
        await Build(marked, new HttpLlmOptions
        {
            BaseUrl = "https://api.example.test/v1",
            Model = "claude-test",
            PromptCacheMarkers = true,
        }).CompleteAsync(Request(new LlmMessage("system", "你是助手。"), new LlmMessage("user", "hi")));

        using (var body = JsonDocument.Parse(marked.RequestBody!))
        {
            var system = body.RootElement.GetProperty("system");
            Assert.Equal(JsonValueKind.Array, system.ValueKind);
            Assert.Equal("你是助手。", system[0].GetProperty("text").GetString());
            Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        }

        var plain = new StubHandler(HttpStatusCode.OK, Reply);
        await Build(plain).CompleteAsync(Request(new LlmMessage("system", "你是助手。"), new LlmMessage("user", "hi")));

        using var plainBody = JsonDocument.Parse(plain.RequestBody!);
        Assert.Equal(JsonValueKind.String, plainBody.RootElement.GetProperty("system").ValueKind);
    }

    [Fact]
    public async Task Listing_models_uses_the_anthropic_headers()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"data":[{"id":"claude-b"},{"id":"claude-a"}],"has_more":false}""");

        var models = await Build(handler).ListModelsAsync();

        Assert.Equal(new[] { "claude-a", "claude-b" }, models);
        Assert.Equal("https://api.example.test/v1/models?limit=1000", handler.Request?.RequestUri?.ToString());
        Assert.Equal("secret", Assert.Single(handler.Request!.Headers.GetValues("x-api-key")));
        Assert.Equal(AnthropicLlmClient.ApiVersion, Assert.Single(handler.Request.Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public async Task A_bare_host_gets_the_version_path()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"type":"message","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""");

        await Build(handler, new HttpLlmOptions
        {
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-test",
        }).CompleteAsync(Request(new LlmMessage("user", "hi")));

        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request?.RequestUri?.ToString());
    }

    [Fact]
    public async Task A_server_that_refuses_streaming_falls_back_to_the_buffered_call()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("stream not supported") },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"type":"message","content":[{"type":"text","text":"buffered"}],"stop_reason":"end_turn"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });

        var deltas = new List<string>();
        var response = await Build(handler).StreamAsync(Request(new LlmMessage("user", "hi")), deltas.Add);

        Assert.Equal("buffered", response.Text);
        Assert.Equal(new[] { "buffered" }, deltas);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public void The_factory_picks_the_client_for_the_configured_protocol()
    {
        using var http = new HttpClient();

        Assert.IsType<HttpLlmClient>(LlmClientFactory.Create(http, new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "m",
        }).Client);

        Assert.IsType<AnthropicLlmClient>(LlmClientFactory.Create(http, new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "m",
            Protocol = "claude",
        }).Client);

        Assert.Throws<ArgumentException>(() => LlmClientFactory.Create(http, new HttpLlmOptions
        {
            BaseUrl = "https://example.test/v1",
            Model = "m",
            Protocol = "gemini",
        }));
    }
}
