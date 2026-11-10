using Partner.Core.Agent.Llm;

namespace Partner.Api.Settings;

/// <summary>
/// One externally exposed reverse-proxy service. Each instance is its own <c>WebApplication</c> on a loopback
/// port: it swaps the client's auth for the upstream key, maps the model name, injects the main memory recall,
/// and forwards the request/response (including tools and SSE) unchanged.
/// </summary>
public sealed record ServiceSettings
{
    /// <summary>Server-generated, immutable id. <c>[a-z0-9-]{1,32}</c>; also names the workspace sub-directory.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name, and the label carried by the summaries this service writes: <c>[对外服务:&lt;Name&gt;]</c>.</summary>
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>Loopback listen port, 1024–65535.</summary>
    public int Port { get; init; }

    /// <summary>Client-facing wire protocol: <see cref="LlmProtocols.OpenAi"/> or <see cref="LlmProtocols.Anthropic"/>.</summary>
    public string Protocol { get; init; } = LlmProtocols.OpenAi;

    /// <summary>The model name clients must send. Mapped to the upstream model name before forwarding.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Context window in tokens. 0 on create means "inherit the main model's ContextTokens".</summary>
    public int ContextTokens { get; init; }

    /// <summary>Custom prompt prepended to requests; empty means the request is left alone.</summary>
    public string Prompt { get; init; } = string.Empty;

    public ServiceRecallSettings Recall { get; init; } = new();

    public ServiceSummarySettings Summary { get; init; } = new();

    public ServiceUpstreamSettings Upstream { get; init; } = new();

    /// <summary>
    /// Write-only bearer token for this instance. Generated on creation; empty on a later write means "keep".
    /// Never returned by the API except once, at creation or rotation.
    /// </summary>
    public string Token { get; init; } = string.Empty;
}

public sealed record ServiceRecallSettings
{
    public bool Enabled { get; init; } = true;

    public int Limit { get; init; } = 8;

    public int TokenLimit { get; init; } = 2000;
}

public sealed record ServiceSummarySettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Idle minutes after the last round before a window is summarized.</summary>
    public int IntervalMinutes { get; init; } = 10;

    /// <summary>Hard ceiling so high-frequency traffic cannot postpone a write forever.</summary>
    public int MaxWaitMinutes { get; init; } = 60;
}

public sealed record ServiceUpstreamSettings
{
    /// <summary>True = use the main model's endpoint, key and model. False = use the fields below.</summary>
    public bool Inherit { get; init; } = true;

    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    /// <summary>Write-only. Empty on a later write means "keep".</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The ceiling this upstream model accepts, used for the Anthropic <c>max_tokens</c> fallback and for the
    /// recall budget. Null = the main model's value. Only meaningful when not inheriting.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Seconds to wait for the upstream response headers. Null = the main model's timeout. Only meaningful when
    /// not inheriting. The streamed body itself is not bounded by it.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// The services section. Like <see cref="AgentsSettings"/>, an <b>absent</b> section (in a settings PUT) means
/// "leave it as it is" — see <c>RuntimeSettings.ServicesSpecified</c>.
/// <para>
/// Value equality is written out because a record holding a list compares it by reference; two identical
/// configs would otherwise look different and every save would restart the hosts.
/// </para>
/// </summary>
public sealed record ServicesSettings
{
    public IReadOnlyList<ServiceSettings> Instances { get; init; } = [];

    /// <summary>Fills the parts a hand-edited or partial file leaves null, so the runtime never sees a null list.</summary>
    public ServicesSettings WithDefaults() => this with
    {
        Instances = (Instances ?? Array.Empty<ServiceSettings>())
            .Where(service => service is not null)
            .Select(service => service with
            {
                Recall = service.Recall ?? new ServiceRecallSettings(),
                Summary = service.Summary ?? new ServiceSummarySettings(),
                Upstream = service.Upstream ?? new ServiceUpstreamSettings(),
            })
            .ToList(),
    };

    public bool Equals(ServicesSettings? other) =>
        other is not null && Instances.SequenceEqual(other.Instances);

    public override int GetHashCode() => HashCode.Combine(Instances.Count);
}

/// <summary>The upstream half of a view: the key is masked as a boolean, never echoed.</summary>
public sealed record ServiceUpstreamView(
    bool Inherit,
    string? BaseUrl,
    string? Model,
    bool ApiKeySet,
    int? MaxTokens,
    int? TimeoutSeconds);

/// <summary>
/// What the API returns for one instance: the same shape as <see cref="ServiceSettings"/> but with the token and
/// the upstream key replaced by <c>TokenSet</c> / <c>ApiKeySet</c> (see <see cref="ModelSettingsView"/>).
/// </summary>
public sealed record ServiceSettingsView(
    string Id,
    string Name,
    bool Enabled,
    int Port,
    string Protocol,
    string Model,
    int ContextTokens,
    string Prompt,
    ServiceRecallSettings Recall,
    ServiceSummarySettings Summary,
    ServiceUpstreamView Upstream,
    bool TokenSet)
{
    public static ServiceSettingsView From(ServiceSettings service) => new(
        service.Id,
        service.Name,
        service.Enabled,
        service.Port,
        LlmProtocols.Normalize(service.Protocol),
        service.Model,
        service.ContextTokens,
        service.Prompt,
        service.Recall,
        service.Summary,
        new ServiceUpstreamView(
            service.Upstream.Inherit,
            service.Upstream.BaseUrl,
            service.Upstream.Model,
            !string.IsNullOrEmpty(service.Upstream.ApiKey),
            service.Upstream.MaxTokens,
            service.Upstream.TimeoutSeconds),
        !string.IsNullOrEmpty(service.Token));
}
