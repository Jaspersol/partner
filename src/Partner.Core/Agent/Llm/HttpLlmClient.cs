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
}

/// <summary>
/// The production <see cref="ILlmClient"/>: an OpenAI-compatible <c>chat/completions</c> call.
/// <para>
/// Deliberately configured from the environment rather than compiled in, and deliberately refuses to
/// pretend: when it is not configured the host does not start a consumer at all (see <see cref="TryFromEnvironment"/>),
/// because a consumer with a stub model would look like a working runtime while doing nothing.
/// </para>
/// </summary>
public sealed class HttpLlmClient : ILlmClient, ITokenCounter
{
    public const string BaseUrlVariable = "PARTNER_LLM_BASE_URL";
    public const string ModelVariable = "PARTNER_LLM_MODEL";
    public const string ApiKeyVariable = "PARTNER_LLM_API_KEY";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Non-ASCII goes out as UTF-8 rather than \uXXXX escapes: escaping roughly triples the bytes of a
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
    /// Reads the configuration from the environment, or explains why it cannot. Split from
    /// <see cref="TryFromEnvironment"/> so a host can report the resolved endpoint without building a client.
    /// </summary>
    public static (HttpLlmOptions? Options, string Reason) OptionsFromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);

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
        }, "已从环境变量配置模型");
    }

    /// <summary>
    /// Builds a client from the environment, or explains why it cannot. The reason is surfaced by the host so
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
            throw new HttpRequestException(
                $"列出模型返回 {(int)response.StatusCode} {response.StatusCode}：{Truncate(detail)}");
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
    /// The local estimate. The synchronous path cannot do I/O, so a caller that wants the model's own count
    /// must use <see cref="CountAsync"/>; the pair is what <c>DualArmTokenCounter</c> combines.
    /// </summary>
    public int Count(string text) => _local.Count(text);

    /// <summary>
    /// The model's own count via <c>POST {base}/tokenize</c> (the llama.cpp-compatible endpoint), memoised.
    /// <para>
    /// Falls back to the local estimate when the server has no such endpoint, and remembers that so a server
    /// without it is not asked again on every turn. <c>usage.prompt_tokens</c> from a completion is deliberately
    /// not used to fill the cache: it totals a whole prompt, which cannot be attributed back to one entry.
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

        using var response = await _http.SendAsync(message, deadline.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            throw new HttpRequestException(
                $"模型返回 {(int)response.StatusCode} {response.StatusCode}：{Truncate(detail)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, deadline.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("模型返回了空响应体");

        return ToResponse(payload);
    }

    private RequestBody BuildBody(LlmRequest request) => new(
        _options.Model,
        request.Messages.Select(ToMessage).ToList(),
        request.Tools.Count == 0
            ? null
            : request.Tools.Select(tool => new ToolPayload(
                "function",
                new FunctionPayload(tool.Name, tool.Description, Schema(tool.ParametersJsonSchema)))).ToList(),
        request.MaxTokens);

    private static JsonElement Schema(string parametersJson)
    {
        // The tool's schema is already JSON; it has to go back out verbatim rather than be re-encoded.
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(parametersJson) ? "{\"type\":\"object\"}" : parametersJson);

        return document.RootElement.Clone();
    }

    private static OutboundMessage ToMessage(LlmMessage message) => new(
        message.Role,
        message.Content,
        message.ToolCalls is null or { Count: 0 }
            ? null
            : message.ToolCalls.Select(call => new OutboundToolCall(
                call.Id,
                "function",
                new OutboundFunction(call.Name, call.ArgumentsJson))).ToList(),
        message.ToolCallId,
        message.Name);

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

        return new LlmResponse(choice.Message.Content, calls, choice.FinishReason ?? "stop");
    }

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "…";

    // ---- wire shapes ------------------------------------------------------------------------------

    private sealed record RequestBody(
        string Model,
        IReadOnlyList<OutboundMessage> Messages,
        IReadOnlyList<ToolPayload>? Tools,
        int? MaxTokens);

    private sealed record OutboundMessage(
        string Role,
        string? Content,
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

    private sealed record ChatResponse(IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(ChatMessage Message, string? FinishReason);

    private sealed record ChatMessage(string? Content, IReadOnlyList<ChatToolCall>? ToolCalls);

    private sealed record ChatToolCall(string Id, ChatFunction Function);

    private sealed record ChatFunction(string Name, string? Arguments);
}
