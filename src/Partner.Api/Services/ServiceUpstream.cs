using System.Net.Http.Headers;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Services;

/// <summary>
/// Where one service actually forwards to, resolved per request so hot config edits take effect. <c>Protocol</c>
/// is the wire protocol of the upstream endpoint; the pass-through model requires it to equal the service's own,
/// because no request is converted between formats.
/// </summary>
internal sealed record UpstreamTarget(
    string BaseUrl,
    string Model,
    string? ApiKey,
    string Protocol,
    int MaxTokens,
    TimeSpan Timeout);

internal static class ServiceUpstream
{
    public static UpstreamTarget Resolve(ServiceSettings settings, RuntimeSettings main)
    {
        if (settings.Upstream.Inherit)
        {
            // Inherit uses the exact same resolution as the consumer, including the environment fallback.
            var (config, _) = ModelConfig.Resolve(main);
            if (config is not null)
            {
                return new UpstreamTarget(
                    config.BaseUrl,
                    config.Model,
                    config.ApiKey,
                    LlmProtocols.Normalize(config.Protocol),
                    config.MaxTokens,
                    config.Timeout);
            }

            return new UpstreamTarget(
                main.Model.BaseUrl ?? string.Empty,
                main.Model.Model ?? string.Empty,
                main.Model.ApiKey,
                LlmProtocols.Normalize(main.Model.Protocol),
                Math.Max(1, main.Model.MaxTokens),
                TimeSpan.FromSeconds(Math.Max(1, main.Model.TimeoutSeconds)));
        }

        return new UpstreamTarget(
            settings.Upstream.BaseUrl ?? string.Empty,
            settings.Upstream.Model ?? string.Empty,
            settings.Upstream.ApiKey,
            // No protocol conversion: a custom upstream speaks whatever the service speaks, so the client's body
            // is only ever correct against that format.
            LlmProtocols.Normalize(settings.Protocol),
            settings.Upstream.MaxTokens ?? Math.Max(1, main.Model.MaxTokens),
            TimeSpan.FromSeconds(settings.Upstream.TimeoutSeconds ?? Math.Max(1, main.Model.TimeoutSeconds)));
    }

    /// <summary>The absolute URL of the upstream completion endpoint.</summary>
    public static string CompletionUrl(UpstreamTarget target)
    {
        var baseUrl = target.BaseUrl.TrimEnd('/');
        return LlmProtocols.Normalize(target.Protocol) == LlmProtocols.Anthropic
            ? AnthropicLlmClient.NormalizeBaseUrl(baseUrl) + "/messages"
            : baseUrl + "/chat/completions";
    }

    /// <summary>
    /// Sets the upstream auth. Client auth headers are never copied: the client's token is a different secret
    /// from the upstream key, and forwarding it would leak one to the other's holder.
    /// </summary>
    public static void ApplyAuth(HttpRequestMessage message, UpstreamTarget target)
    {
        if (LlmProtocols.Normalize(target.Protocol) == LlmProtocols.Anthropic)
        {
            if (!string.IsNullOrEmpty(target.ApiKey))
            {
                message.Headers.TryAddWithoutValidation("x-api-key", target.ApiKey);
            }

            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicLlmClient.ApiVersion);
        }
        else if (!string.IsNullOrEmpty(target.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);
        }
    }
}