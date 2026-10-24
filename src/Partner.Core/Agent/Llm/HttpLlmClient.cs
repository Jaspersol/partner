using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Memory;

namespace Partner.Core.Agent.Llm;

/// <summary>Where the model lives and what to call it.</summary>
public sealed record HttpLlmOptions
{
    /// <summary>Base URL including the API version, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    public string? ApiKey { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Which wire protocol the endpoint speaks; see <see cref="LlmProtocols"/>. The OpenAI client is the
    /// default for backward compatibility, and it ignores the value — <see cref="LlmClientFactory"/> picks
    /// the implementation.
    /// </summary>
    public string Protocol { get; init; } = LlmProtocols.OpenAi;

    /// <summary>
    /// The output ceiling used by protocols that require one on every request (Anthropic's <c>max_tokens</c>).
    /// A request-level <see cref="LlmRequest.MaxTokens"/> wins; this is the fallback.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// The thinking gear sent as <c>reasoning_effort</c>; <see cref="ReasoningEfforts.Off"/> (or blank) sends
    /// nothing.
    /// </summary>
    public string ReasoningEffort { get; init; } = ReasoningEfforts.Default;

    /// <summary>
    /// Marks the system message as cacheable (<c>cache_control: ephemeral</c> on its content part). The system
    /// message is the stable prefix of every request. Off by default: a server that does not know the field may
    /// reject the part array outright.
    /// </summary>
    public bool PromptCacheMarkers { get; init; }
}

/// <summary>
/// The OpenAI-compatible <see cref="ILlmClient"/>: a <c>POST /chat/completions</c> call. The Anthropic wire
/// lives in <see cref="AnthropicLlmClient"/>; <see cref="LlmClientFactory"/> picks between them.
/// <para>
/// When it is not configured the host starts no consumer at all (see <see cref="TryFromEnvironment"/>).
/// </para>
/// </summary>
public sealed class HttpLlmClient : ILlmClient, ITokenCounter, IModelCatalog
{
    public const string BaseUrlVariable = "PARTNER_LLM_BASE_URL";
    public const string ModelVariable = "PARTNER_LLM_MODEL";
    public const string ApiKeyVariable = "PARTNER_LLM_API_KEY";
    public const string ProtocolVariable = "PARTNER_LLM_PROTOCOL";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Non-ASCII goes out as UTF-8 rather than \uXXXX escapes, which roughly triples the bytes of a
        // Chinese conversation. The encoder name warns about HTML contexts; this is a JSON API.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly HttpLlmOptions _options;
    private readonly ApproximateTokenCounter _local = new();
    private readonly TokenCountCache _tokenCounts = new(capacity: 4096);

    /// <summary>Set once the server answers 404/405 for <c>/tokenize</c>: it has no tokenizer endpoint.</summary>
    private volatile bool _tokenizeUnavailable;

    public HttpLlmClient(HttpClient http, HttpLlmOptions options)
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
    }

    public string Model => _options.Model;

    /// <summary>
    /// Reads the configuration from the environment, or explains why it cannot. Separate from
    /// <see cref="TryFromEnvironment"/> so a host can report the resolved endpoint without building a client.
    /// </summary>
    public static (HttpLlmOptions? Options, string Reason) OptionsFromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        var protocol = Environment.GetEnvironmentVariable(ProtocolVariable);

        if (!string.IsNullOrWhiteSpace(protocol) && !LlmProtocols.IsValid(protocol))
        {
            return (null, $"未配置模型：{ProtocolVariable} 必须是 {string.Join(" 或 ", LlmProtocols.Values)}");
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                missing.Add(BaseUrlVariable);
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                missing.Add(ModelVariable);
            }

            return (null, $"未配置模型：缺少 {string.Join(" 与 ", missing)}");
        }

        return (new HttpLlmOptions
        {
            BaseUrl = baseUrl.TrimEnd('/'),
            Model = model,
            ApiKey = apiKey,
            Protocol = LlmProtocols.Normalize(protocol),
        }, "已从环境变量配置模型");
    }

    /// <summary>
    /// Builds a client from the environment, or explains why it cannot. The host surfaces the reason so
    /// "no consumer running" is never a silent state.
    /// </summary>
    public static (HttpLlmClient? Client, string Reason) TryFromEnvironment(HttpClient? http = null)
    {
        var (options, reason) = OptionsFromEnvironment();
        if (options is null)
        {
            return (null, reason);
        }

        var client = http ?? new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan; // the per-request token governs, not a second clock

        return (new HttpLlmClient(client, options), reason);
    }

    /// <summary>
    /// The model ids the endpoint advertises (<c>GET {base}/models</c>, the OpenAI-compatible listing). An
    /// endpoint that does not implement it fails with the provider's own message rather than an empty list,
    /// because "no models" and "cannot ask" are different answers for the caller.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/models");

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

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

    /// <summary>
    /// What is already known about this text, without touching the network: previously counted values, and the
    /// local estimate for anything new. A session view must not wait on or fail because of a model endpoint.
    /// </summary>
    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return _tokenCounts.TryGet(text, out var known) ? known : _local.Count(text);
    }

    /// <summary>
    /// The model's own count via <c>POST {base}/tokenize</c> (the llama.cpp-compatible endpoint), memoised.
    /// <para>
    /// Falls back to the local estimate when the server has no such endpoint, and remembers that so it is not
    /// asked again. <c>usage.prompt_tokens</c> from a completion is deliberately not used to fill the cache: it
    /// totals a whole prompt, which cannot be attributed back to one entry.
    /// </para>
    /// </summary>
    public async ValueTask<int> CountAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (_tokenCounts.TryGet(text, out var cached))
        {
            return cached;
        }

        if (_tokenizeUnavailable)
        {
            return _local.Count(text);
        }

        try
        {
            var counted = await TokenizeAsync(text, ct).ConfigureAwait(false);
            _tokenCounts.Set(text, counted);
            return counted;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // The endpoint does not exist on this server: that is a permanent answer, not a transient failure.
            _tokenizeUnavailable = true;
            return _local.Count(text);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            // A canceled turn must stop, not quietly continue with an estimate. A timeout looks the same by
            // type, so the caller's token decides.
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Transient: fall back for this call, but keep asking — one blip should not disable native counting.
            return _local.Count(text);
        }
    }

    private async Task<int> TokenizeAsync(string text, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/tokenize")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { content = text }, Json),
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json")),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        using var response = await _http.SendAsync(message, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TokenizeResponse>(Json, deadline.Token).ConfigureAwait(false);

        return payload?.Tokens?.Count ?? _local.Count(text);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Buffered rather than JsonContent: a non-buffered content makes HttpClient use chunked encoding, and a
        // request with a declared length is friendlier to the proxies and local servers this talks to.
        var json = JsonSerializer.Serialize(BuildBody(request), Json);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A refused connection and a refused request look the same to the person watching, so both go out
            // in the same shape. The configured limit rides along so the reader knows which number to raise.
            throw ModelCallFailure.From("模型补全", error, _options.Timeout);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                throw ModelCallFailure.Rejected("模型补全", response.StatusCode, detail, RequestIdOf(response));
            }

            var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, deadline.Token).ConfigureAwait(false)
                ?? throw ModelCallFailure.Unreadable("模型补全", "响应体为空");

            return ToResponse(payload);
        }
    }

    /// <summary>
    /// Streams the completion over SSE (<c>stream: true</c>), reporting text as it arrives and returning the
    /// assembled response.
    /// <para>
    /// The returned value — not the callbacks — is what the caller records, so a stream that dies halfway
    /// leaves no half-written reply in the session. A server that refuses <c>stream: true</c> still answers
    /// the buffered call, so a 4xx falls back to <see cref="CompleteAsync"/> rather than failing the turn.
    /// </para>
    /// </summary>
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

        var json = JsonSerializer.Serialize(BuildBody(request) with { Stream = true }, Json);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);

        // ResponseHeadersRead, not the default: the body is an open stream and buffering it would wait for the
        // whole reply — which is the thing this method exists to avoid.
        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                // The interface's contract still holds on this path: non-empty text is reported once.
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

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCallBuilder>();
        var finish = "stop";
        var promptTokens = 0;
        var completionTokens = 0;
        var nextAnonymousIndex = 0;

        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
        {
            // SSE frames: "data: {...}" per chunk, blank lines between, ": comment" for keepalives.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                break;
            }

            ChatChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatChunk>(payload, Json);
            }
            catch (JsonException)
            {
                // A frame that is not a chunk we understand (a server's own extension) must not kill the turn.
                continue;
            }

            // The usage rides on a frame of its own (empty choices) at the end of the stream, when the server sends it.
            if (chunk?.Usage is { } usage)
            {
                promptTokens = usage.PromptTokens;
                completionTokens = usage.CompletionTokens;
            }

            if (chunk?.Choices is not { Count: > 0 })
            {
                continue;
            }

            var choice = chunk.Choices[0];

            if (!string.IsNullOrEmpty(choice.Delta?.Content))
            {
                text.Append(choice.Delta.Content);
                onText(choice.Delta.Content);
            }

            // Reasoning arrives as its own delta channel, before any answer text, and only when a thinking
            // gear asked for it.
            if (choice.Delta is { } delta)
            {
                var fragment = delta.ReasoningContent ?? delta.Reasoning;
                if (!string.IsNullOrEmpty(fragment))
                {
                    reasoning.Append(fragment);
                    onReasoning(fragment);
                }
            }

            if (choice.Delta?.ToolCalls is { Count: > 0 })
            {
                foreach (var fragment in choice.Delta.ToolCalls)
                {
                    // Tool calls arrive as fragments keyed by index: the name in the first frame, the JSON
                    // arguments split across the rest.
                    var index = fragment.Index ?? nextAnonymousIndex++;
                    if (!calls.TryGetValue(index, out var builder))
                    {
                        builder = new ToolCallBuilder();
                        calls[index] = builder;
                    }

                    if (!string.IsNullOrEmpty(fragment.Id))
                    {
                        builder.Id = fragment.Id;
                    }

                    if (!string.IsNullOrEmpty(fragment.Function?.Name))
                    {
                        builder.Name = fragment.Function.Name;
                    }

                    if (!string.IsNullOrEmpty(fragment.Function?.Arguments))
                    {
                        builder.Arguments.Append(fragment.Function.Arguments);
                    }
                }
            }

            if (!string.IsNullOrEmpty(choice.FinishReason))
            {
                finish = choice.FinishReason;
            }
        }

        var assembled = calls.Values
            .Where(builder => !string.IsNullOrWhiteSpace(builder.Name))
            .Select(builder => new LlmToolCall(
                builder.Id ?? $"call_{builder.Name}",
                builder.Name!,
                builder.Arguments.Length == 0 ? "{}" : builder.Arguments.ToString()))
            .ToList();

        return new LlmResponse(
            text.Length == 0 ? null : text.ToString(),
            assembled,
            finish,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Reasoning: reasoning.Length == 0 ? null : reasoning.ToString());
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }

    private RequestBody BuildBody(LlmRequest request) => new(
        _options.Model,
        request.Messages.Select(message => ToMessage(message, _options.PromptCacheMarkers)).ToList(),
        request.Tools.Count == 0
            ? null
            : request.Tools.Select(tool => new ToolPayload(
                "function",
                new FunctionPayload(tool.Name, tool.Description, Schema(tool.ParametersJsonSchema)))).ToList(),
        request.MaxTokens,
        // The request wins when it carries a gear: a turn may have been sent with one chosen per message.
        // Absent means "whatever the client was configured with", which is the single global default.
        ReasoningEfforts.Wire(request.ReasoningEffort ?? _options.ReasoningEffort));

    private static JsonElement Schema(string parametersJson)
    {
        // The tool's schema is already JSON; it has to go back out verbatim rather than be re-encoded.
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(parametersJson) ? "{\"type\":\"object\"}" : parametersJson);

        return document.RootElement.Clone();
    }

    private static OutboundMessage ToMessage(LlmMessage message, bool cacheMarkers) => new(
        message.Role,
        cacheMarkers && message.Role == "system" && !string.IsNullOrEmpty(message.Content)
            ? new List<OutboundContentPart> { new("text", message.Content, null, new OutboundCacheControl("ephemeral")) }
            : ToContent(message),
        message.ToolCalls is null or { Count: 0 }
            ? null
            : message.ToolCalls.Select(call => new OutboundToolCall(
                call.Id,
                "function",
                new OutboundFunction(call.Name, call.ArgumentsJson))).ToList(),
        message.ToolCallId,
        message.Name);

    /// <summary>
    /// A message's content: a plain string, or the OpenAI-compatible part array when images are attached.
    /// <para>
    /// The data URI is built here rather than stored: it is about a third larger than the bytes, and only the
    /// request needs it. A server that predates multimodal content rejects the request rather than silently
    /// sending the text without what the user attached.
    /// </para>
    /// </summary>
    private static object? ToContent(LlmMessage message)
    {
        if (message.Images is not { Count: > 0 })
        {
            return message.Content;
        }

        var parts = new List<OutboundContentPart>();
        if (!string.IsNullOrEmpty(message.Content))
        {
            parts.Add(new OutboundContentPart("text", message.Content, null));
        }

        foreach (var image in message.Images)
        {
            parts.Add(new OutboundContentPart("image_url", null, new OutboundImageUrl(image)));
        }

        return parts;
    }

    private static LlmResponse ToResponse(ChatResponse payload)
    {
        var choice = payload.Choices is { Count: > 0 } ? payload.Choices[0] : null;
        if (choice is null)
        {
            throw new InvalidOperationException("模型响应里没有 choices");
        }

        var calls = choice.Message.ToolCalls is { Count: > 0 }
            ? choice.Message.ToolCalls
                .Select(call => new LlmToolCall(
                    call.Id,
                    call.Function.Name,
                    string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments))
                .ToList()
            : (IReadOnlyList<LlmToolCall>)Array.Empty<LlmToolCall>();

        // A buffered reply carries the same reasoning as a streamed one: the endpoint sends it once, in the
        // message, rather than as deltas.
        var reasoning = choice.Message.ReasoningContent ?? choice.Message.Reasoning;

        return new LlmResponse(
            choice.Message.Content,
            calls,
            choice.FinishReason ?? "stop",
            PromptTokens: payload.Usage?.PromptTokens ?? 0,
            CompletionTokens: payload.Usage?.CompletionTokens ?? 0,
            Reasoning: string.IsNullOrEmpty(reasoning) ? null : reasoning);
    }

    /// <summary>
    /// The provider's own id for the request, from wherever they put it. It is the first thing their support
    /// asks for, and a failure that has thrown it away can only be re-created.
    /// </summary>
    private static string? RequestIdOf(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "request-id", "x-amzn-requestid", "cf-ray" })
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

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "…";

    // Wire shapes.

    private sealed record RequestBody(
        string Model,
        IReadOnlyList<OutboundMessage> Messages,
        IReadOnlyList<ToolPayload>? Tools,
        int? MaxTokens,
        string? ReasoningEffort,
        bool? Stream = null);

    private sealed record OutboundContentPart(
        string Type,
        string? Text,
        OutboundImageUrl? ImageUrl,
        OutboundCacheControl? CacheControl = null);

    private sealed record OutboundCacheControl(string Type);

    private sealed record OutboundImageUrl(string Url);

    private sealed record OutboundMessage(
        string Role,
        object? Content,
        IReadOnlyList<OutboundToolCall>? ToolCalls,
        string? ToolCallId,
        string? Name);

    private sealed record OutboundToolCall(string Id, string Type, OutboundFunction Function);

    private sealed record OutboundFunction(string Name, string Arguments);

    private sealed record ToolPayload(string Type, FunctionPayload Function);

    private sealed record FunctionPayload(string Name, string Description, JsonElement Parameters);

    private sealed record TokenizeResponse(IReadOnlyList<int>? Tokens);

    private sealed record ModelsResponse(IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(string Id);

    private sealed record ChatResponse(IReadOnlyList<ChatChoice>? Choices, ChatUsage? Usage = null);

    private sealed record ChatChunk(IReadOnlyList<ChatChunkChoice>? Choices, ChatUsage? Usage = null);

    /// <summary>What the server says the request cost.</summary>
    private sealed record ChatUsage(int PromptTokens, int CompletionTokens);

    private sealed record ChatChunkChoice(ChatDelta? Delta, string? FinishReason);

    /// <summary>
    /// One delta. Reasoning is a channel of its own: the endpoint sends it before the answer, under whichever
    /// key it uses, and a delta may carry either, both, or neither.
    /// </summary>
    private sealed record ChatDelta(
        string? Content,
        IReadOnlyList<ChatDeltaToolCall>? ToolCalls,
        string? ReasoningContent,
        string? Reasoning);

    private sealed record ChatDeltaToolCall(int? Index, string? Id, ChatFunction? Function);

    private sealed record ChatChoice(ChatMessage Message, string? FinishReason);

    private sealed record ChatMessage(
        string? Content,
        IReadOnlyList<ChatToolCall>? ToolCalls,
        string? ReasoningContent,
        string? Reasoning);

    private sealed record ChatToolCall(string Id, ChatFunction Function);

    private sealed record ChatFunction(string Name, string? Arguments);
}
