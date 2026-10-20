using Partner.Core.Agent.Llm;

namespace Partner.Api.Settings;

/// <summary>
/// The model endpoint the consumer should use, and where that decision came from.
/// <para>
/// Precedence: a complete model section in settings wins, the environment is the fallback, and when neither is
/// complete the consumer does not start. The origin travels with the config so the UI can say which one is in
/// effect.
/// </para>
/// </summary>
public sealed record ModelConfig(
    string BaseUrl,
    string Model,
    string? ApiKey,
    TimeSpan Timeout,
    string Origin,
    int ContextTokens = 128000,
    int MaxTokens = 32000,
    string ReasoningEffort = ReasoningEfforts.Default,
    bool PromptCache = false)
{
    public static (ModelConfig? Config, string Reason) Resolve(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var fromSettings = settings.Model;
        if (!string.IsNullOrWhiteSpace(fromSettings.BaseUrl) && !string.IsNullOrWhiteSpace(fromSettings.Model))
        {
            return (new ModelConfig(
                fromSettings.BaseUrl.TrimEnd('/'),
                fromSettings.Model,
                fromSettings.ApiKey,
                TimeSpan.FromSeconds(fromSettings.TimeoutSeconds),
                "设置",
                fromSettings.ContextTokens,
                fromSettings.MaxTokens,
                fromSettings.ReasoningEffort,
                fromSettings.PromptCache), "运行中（设置）");
        }

        var (options, reason) = HttpLlmClient.OptionsFromEnvironment();
        if (options is null)
        {
            return (null, $"{reason}；设置里也没有填 model.baseUrl 与 model.model");
        }

        return (new ModelConfig(
            options.BaseUrl,
            options.Model,
            options.ApiKey,
            options.Timeout,
            "环境变量",
            ReasoningEffort: options.ReasoningEffort), "运行中（环境变量）");
    }

    /// <summary>True when the two describe a different endpoint, so a restart is warranted.</summary>
    public static bool Differs(ModelSettings left, ModelSettings right) =>
        !string.Equals(left.BaseUrl, right.BaseUrl, StringComparison.Ordinal)
        || !string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        || !string.Equals(left.ApiKey, right.ApiKey, StringComparison.Ordinal)
        || left.PromptCache != right.PromptCache
        || left.TimeoutSeconds != right.TimeoutSeconds
        // The turn window and output ceiling are part of the same configuration: changing either has to
        // reach the running runner, which only happens through a restart.
        || left.ContextTokens != right.ContextTokens
        || left.MaxTokens != right.MaxTokens
        // Whether the model takes pictures decides which tools exist (the screenshot fallback), and the tool
        // table is built at start — so this belongs with the rest of what a restart is for.
        || left.SupportsImages != right.SupportsImages
        // The thinking gear is part of the client the consumer builds, so it takes a restart like the rest of
        // the endpoint's shape — unlike the desktop approval gear, which governance reads live.
        || !string.Equals(
            ReasoningEfforts.Normalize(left.ReasoningEffort),
            ReasoningEfforts.Normalize(right.ReasoningEffort),
            StringComparison.Ordinal);
}
