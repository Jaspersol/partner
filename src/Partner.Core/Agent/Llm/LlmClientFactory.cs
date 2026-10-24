using Partner.Core.Memory;

namespace Partner.Core.Agent.Llm;

/// <summary>One configured endpoint: the client, its token counter and its model catalog.</summary>
public sealed record LlmClientSet(ILlmClient Client, ITokenCounter Tokens, IModelCatalog Catalog);

/// <summary>
/// Builds the client that speaks the configured protocol. Callers stay protocol-neutral: they ask for a
/// completion, a token count, or a model listing and get the same interface whichever wire it uses.
/// </summary>
public static class LlmClientFactory
{
    public static LlmClientSet Create(HttpClient http, HttpLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        var protocol = LlmProtocols.Normalize(options.Protocol);
        var normalized = options with { Protocol = protocol };

        return protocol switch
        {
            LlmProtocols.OpenAi => From(new HttpLlmClient(http, normalized)),
            LlmProtocols.Anthropic => From(new AnthropicLlmClient(http, normalized)),
            _ => throw new ArgumentException(
                $"未知的模型接口格式「{options.Protocol}」：只能是 {string.Join(" / ", LlmProtocols.Values)}",
                nameof(options)),
        };
    }

    private static LlmClientSet From(HttpLlmClient client) => new(client, client, client);

    private static LlmClientSet From(AnthropicLlmClient client) => new(client, client, client);
}
