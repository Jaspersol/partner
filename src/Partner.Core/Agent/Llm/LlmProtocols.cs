namespace Partner.Core.Agent.Llm;

/// <summary>
/// The wire protocol a model endpoint speaks. The conversation model in this repository is provider-neutral;
/// the protocol decides how it is translated on the way out and back.
/// </summary>
public static class LlmProtocols
{
    /// <summary>OpenAI-compatible <c>POST /chat/completions</c>.</summary>
    public const string OpenAi = "openai";

    /// <summary>Anthropic <c>POST /messages</c> (the Claude Messages API).</summary>
    public const string Anthropic = "anthropic";

    /// <summary>The values the settings UI offers, in display order.</summary>
    public static IReadOnlyList<string> Values { get; } = new[] { OpenAi, Anthropic };

    /// <summary>
    /// Trimmed and lower case, with <c>claude</c> accepted as the everyday name for <see cref="Anthropic"/>.
    /// A blank value means the historical default, an unknown word is kept so validation can report it.
    /// </summary>
    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "" => OpenAi,
            "claude" => Anthropic,
            _ => normalized,
        };
    }

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return normalized is OpenAi or Anthropic;
    }
}
