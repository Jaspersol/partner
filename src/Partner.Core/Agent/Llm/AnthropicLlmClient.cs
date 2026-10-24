using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Memory;

namespace Partner.Core.Agent.Llm;

/// <summary>
/// The Anthropic Messages API (<c>POST {base}/messages</c>): the wire the Claude API and Anthropic-compatible
/// gateways speak.
/// <para>
/// The conversation model here is OpenAI-shaped, so this client translates in both directions: system prompts
/// become the top-level <c>system</c>, tool results become <c>tool_result</c> blocks in a <b>user</b> turn, and
/// tool calls become <c>tool_use</c> blocks in the assistant turn.
/// </para>
/// <para>
/// Extended thinking is deliberately not requested: Anthropic requires the signed thinking blocks to be
/// replayed when a tool-use turn continues, and the turn runner keeps only text, so a replayed conversation
/// would be rejected. A proxy that returns thinking blocks anyway still has them surfaced as reasoning.
/// </para>
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient, ITokenCounter, IModelCatalog
{
    /// <summary>The API version header Anthropic requires on every request.</summary>
    public const string ApiVersion = "2023-06-01";

    private const int DefaultMaxTokens = 4096;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly HttpLlmOptions _options;
    private readonly string _baseUrl;
    private readonly ApproximateTokenCounter _local = new();

    public AnthropicLlmClient(HttpClient http, HttpLlmOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new ArgumentException("BaseUrl is required", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("Model is required", nameof(options));
        }

        _baseUrl = NormalizeBaseUrl(options.BaseUrl);
    }

    public string Model => _options.Model;

    /// <summary>
    /// The base URL with the API version attached when only a host was given. Anthropic clients routinely
    /// paste <c>https://api.anthropic.com</c>; the messages route lives under <c>/v1</c>, and a request to the
    /// bare host answers 404.
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/")
                ? trimmed + "/v1"
                : trimmed;
    }

    /// <inheritdoc />
    public int Count(string text) => _local.Count(text);

    /// <summary>
    /// The local estimate. Anthropic's <c>count_tokens</c> endpoint prices a whole request (messages, system,
    /// tools), which cannot be attributed back to the one text a budget asks about, so the counter that feeds
    /// the budget stays the local approximation.
    /// </summary>
    public ValueTask<int> CountAsync(string text, CancellationToken ct = default) =>
        ValueTask.FromResult(_local.Count(text));

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        // The Anthropic listing pages at 20 by default; one page keeps the dropdown useful without a pager.
        using var message = CreateRequest(HttpMethod.Get, "/models?limit=1000");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        using var response = await _http.SendAsync(message, deadline.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            throw ModelCallFailure.Rejected("列出模型", response.StatusCode, detail, RequestIdOf(response));
        }

        var payload = await response.Content.ReadFromJsonAsync<ModelsResponse>(Json, deadline.Token).ConfigureAwait(false);

        return payload?.Data is { Count: > 0 }
            ? payload.Data
                .Select(model => model.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
            : Array.Empty<string>();
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = JsonSerializer.Serialize(BuildBody(request, stream: false), Json);
        using var message = CreateRequest(HttpMethod.Post, "/messages", json);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ModelCallFailure.From("模型补全", error, _options.Timeout);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                throw ModelCallFailure.Rejected("模型补全", response.StatusCode, detail, RequestIdOf(response));
            }

            var payload = await response.Content.ReadFromJsonAsync<MessagesResponse>(Json, deadline.Token).ConfigureAwait(false)
                ?? throw ModelCallFailure.Unreadable("模型补全", "响应体为空", AnthropicHint);

            return ToResponse(payload);
        }
    }

    /// <inheritdoc />
    public Task<LlmResponse> StreamAsync(
        LlmRequest request,
        Action<string> onText,
        CancellationToken ct = default) =>
        StreamWithReasoningAsync(request, onText, _ => { }, ct);

    /// <inheritdoc />
    public async Task<LlmResponse> StreamWithReasoningAsync(
        LlmRequest request,
        Action<string> onText,
        Action<string> onReasoning,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onText);
        ArgumentNullException.ThrowIfNull(onReasoning);

        var json = JsonSerializer.Serialize(BuildBody(request, stream: true), Json);
        using var message = CreateRequest(HttpMethod.Post, "/messages", json);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                // Same contract as the OpenAI client: a server that refuses streaming still answers the
                // buffered call, and non-empty text is reported exactly once.
                var buffered = await CompleteAsync(request, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(buffered.Text))
                {
                    onText(buffered.Text);
                }

                return buffered;
            }

            var detail = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            throw ModelCallFailure.Rejected("模型补全", response.StatusCode, detail, RequestIdOf(response));
        }

        var blocks = new SortedDictionary<int, BlockBuilder>();
        var finish = "stop";
        var promptTokens = 0;
        var completionTokens = 0;

        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
        {
            // SSE frames: "event: ..." then "data: {...}", blank line between. The payload's own `type`
            // says what it is, so the event-name line needs no bookkeeping.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            StreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<StreamEvent>(payload, Json);
            }
            catch (JsonException)
            {
                // An extension frame must not kill the turn.
                continue;
            }

            switch (evt?.Type)
            {
                case "message_start":
                    if (evt.Message?.Usage is { } startUsage)
                    {
                        promptTokens = startUsage.TotalInputTokens;
                        completionTokens = startUsage.OutputTokens;
                    }

                    break;

                case "content_block_start":
                    if (evt.Index is { } startIndex)
                    {
                        blocks[startIndex] = new BlockBuilder(evt.ContentBlock?.Type ?? "text")
                        {
                            Id = evt.ContentBlock?.Id,
                            Name = evt.ContentBlock?.Name,
                        };
                    }

                    break;

                case "content_block_delta":
                    if (evt.Delta is not { } delta)
                    {
                        break;
                    }

                    if (!blocks.TryGetValue(evt.Index ?? 0, out var block))
                    {
                        block = new BlockBuilder(BlockTypeOf(delta.Type));
                        blocks[evt.Index ?? 0] = block;
                    }

                    if (!string.IsNullOrEmpty(delta.Text))
                    {
                        block.Text.Append(delta.Text);
                        onText(delta.Text);
                    }

                    if (!string.IsNullOrEmpty(delta.Thinking))
                    {
                        block.Thinking.Append(delta.Thinking);
                        onReasoning(delta.Thinking);
                    }

                    if (!string.IsNullOrEmpty(delta.PartialJson))
                    {
                        block.InputJson.Append(delta.PartialJson);
                    }

                    break;

                case "message_delta":
                    if (!string.IsNullOrEmpty(evt.Delta?.StopReason))
                    {
                        finish = FinishOf(evt.Delta.StopReason);
                    }

                    if (evt.Usage is { } endUsage)
                    {
                        completionTokens = endUsage.OutputTokens;
                    }

                    break;

                case "error":
                    throw ModelCallFailure.Unreadable(
                        "模型补全",
                        evt.Error?.Message ?? "流在返回途中报错",
                        AnthropicHint);
            }
        }

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<LlmToolCall>();

        foreach (var (_, block) in blocks)
        {
            switch (block.Type)
            {
                case "text":
                    text.Append(block.Text);
                    break;

                case "thinking":
                    reasoning.Append(block.Thinking);
                    break;

                case "tool_use" when !string.IsNullOrWhiteSpace(block.Name):
                    calls.Add(new LlmToolCall(
                        block.Id ?? $"toolu_{block.Name}",
                        block.Name!,
                        block.InputJson.Length == 0 ? "{}" : block.InputJson.ToString()));
                    break;
            }
        }

        return new LlmResponse(
            text.Length == 0 ? null : text.ToString(),
            calls,
            finish,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Reasoning: reasoning.Length == 0 ? null : reasoning.ToString());
    }

    private static string BlockTypeOf(string? deltaType) => deltaType switch
    {
        "thinking_delta" => "thinking",
        "input_json_delta" => "tool_use",
        _ => "text",
    };

    /// <summary>Anthropic's stop reasons folded into the three the turn runner understands.</summary>
    private static string FinishOf(string? stopReason) => stopReason switch
    {
        "tool_use" => "tool_calls",
        "max_tokens" => "length",
        _ => "stop",
    };

    private LlmResponse ToResponse(MessagesResponse payload)
    {
        if (payload.Type == "error" || payload.Error is not null)
        {
            throw ModelCallFailure.Unreadable(
                "模型补全",
                payload.Error?.Message ?? "端点返回了 error 而不是 message",
                AnthropicHint);
        }

        if (payload.Content is null)
        {
            throw ModelCallFailure.Unreadable("模型补全", "响应里没有 content", AnthropicHint);
        }

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<LlmToolCall>();

        foreach (var block in payload.Content)
        {
            switch (block.Type)
            {
                case "text":
                    text.Append(block.Text);
                    break;

                case "thinking":
                    reasoning.Append(block.Thinking);
                    break;

                case "tool_use" when !string.IsNullOrWhiteSpace(block.Name):
                    calls.Add(new LlmToolCall(
                        block.Id ?? $"toolu_{block.Name}",
                        block.Name!,
                        block.Input is { } input && input.ValueKind != JsonValueKind.Undefined
                            ? input.GetRawText()
                            : "{}"));
                    break;
            }
        }

        return new LlmResponse(
            text.Length == 0 ? null : text.ToString(),
            calls,
            FinishOf(payload.StopReason),
            PromptTokens: payload.Usage?.TotalInputTokens ?? 0,
            CompletionTokens: payload.Usage?.OutputTokens ?? 0,
            Reasoning: reasoning.Length == 0 ? null : reasoning.ToString());
    }

    private MessagesBody BuildBody(LlmRequest request, bool stream)
    {
        var (system, messages) = BuildConversation(request.Messages);
        var maxTokens = request.MaxTokens ?? _options.MaxOutputTokens ?? DefaultMaxTokens;
        if (maxTokens <= 0)
        {
            maxTokens = DefaultMaxTokens;
        }

        return new MessagesBody(
            _options.Model,
            maxTokens,
            system,
            messages,
            request.Tools.Count == 0
                ? null
                : request.Tools
                    .Select(tool => new ToolPayload(tool.Name, tool.Description, Schema(tool.ParametersJsonSchema)))
                    .ToList(),
            stream ? true : null);
    }

    /// <summary>
    /// Splits the neutral history into Anthropic's system prompt and alternating user/assistant turns.
    /// Consecutive messages of the same role are merged because a tool result and the pictures a tool returned
    /// are two user messages here and one turn on the wire.
    /// </summary>
    private (object? System, IReadOnlyList<OutboundMessage> Messages) BuildConversation(
        IReadOnlyList<LlmMessage> source)
    {
        var systems = new List<string>();
        var turns = new List<MutableTurn>();

        void Append(string role, List<ContentPart> parts)
        {
            if (parts.Count == 0)
            {
                return;
            }

            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1].Content.AddRange(parts);
                return;
            }

            turns.Add(new MutableTurn(role, parts));
        }

        foreach (var message in source)
        {
            switch (message.Role)
            {
                case "system":
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        systems.Add(message.Content);
                    }

                    break;

                case "tool":
                    Append("user", new List<ContentPart> { ToolResultPart(message) });
                    break;

                case "assistant":
                {
                    var parts = new List<ContentPart>();
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        parts.Add(TextPart(message.Content));
                    }

                    if (message.ToolCalls is { Count: > 0 })
                    {
                        parts.AddRange(message.ToolCalls.Select(ToolUsePart));
                    }

                    Append("assistant", parts);
                    break;
                }

                default:
                    Append("user", UserParts(message));
                    break;
            }
        }

        object? system = systems.Count == 0
            ? null
            : _options.PromptCacheMarkers
                ? new List<ContentPart>
                {
                    new("text", Text: string.Join("\n\n", systems), CacheControl: new OutboundCacheControl("ephemeral")),
                }
                : string.Join("\n\n", systems);

        return (system, turns.Select(turn => new OutboundMessage(turn.Role, turn.Content)).ToList());
    }

    private static ContentPart TextPart(string text) => new("text", Text: text);

    private static ContentPart ToolResultPart(LlmMessage message) => new(
        "tool_result",
        ToolUseId: message.ToolCallId ?? string.Empty,
        Content: message.Content ?? string.Empty);

    private static ContentPart ToolUsePart(LlmToolCall call)
    {
        var input = ParseInput(call.ArgumentsJson);
        return new ContentPart("tool_use", Id: call.Id, Name: call.Name, Input: input);
    }

    private static JsonElement ParseInput(string? argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // The model produced something that is not JSON; an empty object keeps the history sendable. The
            // call itself already failed at the tool boundary, so nothing is silently executed.
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private static List<ContentPart> UserParts(LlmMessage message)
    {
        var parts = new List<ContentPart>();
        if (!string.IsNullOrEmpty(message.Content))
        {
            parts.Add(TextPart(message.Content));
        }

        if (message.Images is { Count: > 0 })
        {
            foreach (var image in message.Images)
            {
                if (ImagePart(image) is { } part)
                {
                    parts.Add(part);
                }
            }
        }

        return parts;
    }

    /// <summary>
    /// Turns the stored form — a <c>data:</c> URI — into the base64 source block Anthropic takes. A plain
    /// http(s) URL is sent as a URL source, which the API also accepts.
    /// </summary>
    private static ContentPart? ImagePart(string image)
    {
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = image.IndexOf(',');
            if (comma < 0)
            {
                return null;
            }

            var meta = image[5..comma];
            var semicolon = meta.IndexOf(';');
            var mediaType = semicolon >= 0 ? meta[..semicolon] : meta;
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                mediaType = "image/png";
            }

            return new ContentPart(
                "image",
                Source: new OutboundSource("base64", mediaType, image[(comma + 1)..], null));
        }

        if (Uri.TryCreate(image, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ContentPart("image", Source: new OutboundSource("url", null, null, image));
        }

        return null;
    }

    private static JsonElement Schema(string parametersJson)
    {
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(parametersJson) ? "{\"type\":\"object\"}" : parametersJson);
        return document.RootElement.Clone();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? json = null)
    {
        var message = new HttpRequestMessage(method, _baseUrl + path);
        if (json is not null)
        {
            message.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            // Anthropic authenticates with x-api-key, not a bearer token.
            message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        }

        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        return message;
    }

    private const string AnthropicHint = "确认地址以 /v1 结尾、接口格式选的是 Anthropic，并且端点支持 /messages";

    private static string? RequestIdOf(HttpResponseMessage response)
    {
        foreach (var header in new[] { "request-id", "x-request-id", "cf-ray" })
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private sealed class MutableTurn(string role, List<ContentPart> content)
    {
        public string Role { get; } = role;

        public List<ContentPart> Content { get; } = content;
    }

    private sealed class BlockBuilder(string type)
    {
        public string Type { get; } = type;

        public string? Id { get; init; }

        public string? Name { get; init; }

        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public StringBuilder InputJson { get; } = new();
    }

    // Wire shapes.

    private sealed record MessagesBody(
        string Model,
        int MaxTokens,
        object? System,
        IReadOnlyList<OutboundMessage> Messages,
        IReadOnlyList<ToolPayload>? Tools,
        bool? Stream);

    private sealed record OutboundMessage(string Role, IReadOnlyList<ContentPart> Content);

    private sealed record ContentPart(
        string Type,
        string? Text = null,
        string? Id = null,
        string? Name = null,
        JsonElement? Input = null,
        string? ToolUseId = null,
        string? Content = null,
        OutboundSource? Source = null,
        OutboundCacheControl? CacheControl = null);

    private sealed record OutboundSource(string Type, string? MediaType, string? Data, string? Url);

    private sealed record OutboundCacheControl(string Type);

    private sealed record ToolPayload(string Name, string Description, JsonElement InputSchema);

    private sealed record ModelsResponse(IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(string Id);

    private sealed record MessagesResponse(
        string? Type,
        IReadOnlyList<ResponseContent>? Content,
        string? StopReason,
        ResponseUsage? Usage,
        StreamError? Error);

    private sealed record ResponseContent(
        string? Type,
        string? Text,
        string? Thinking,
        string? Id,
        string? Name,
        JsonElement? Input);

    /// <summary>
    /// What the request cost. Cached input is billed separately but is still context the model read, so the
    /// budget sees the total rather than only the uncached part.
    /// </summary>
    private sealed record ResponseUsage(
        int InputTokens,
        int OutputTokens,
        int CacheCreationInputTokens = 0,
        int CacheReadInputTokens = 0)
    {
        public int TotalInputTokens => InputTokens + CacheCreationInputTokens + CacheReadInputTokens;
    }

    private sealed record StreamError(string? Type, string? Message);

    private sealed record StreamEvent(
        string? Type,
        StreamMessage? Message,
        int? Index,
        ResponseContent? ContentBlock,
        StreamDelta? Delta,
        ResponseUsage? Usage,
        StreamError? Error);

    private sealed record StreamMessage(
        IReadOnlyList<ResponseContent>? Content,
        string? StopReason,
        ResponseUsage? Usage);

    private sealed record StreamDelta(
        string? Type,
        string? Text,
        string? Thinking,
        string? PartialJson,
        string? StopReason);
}
