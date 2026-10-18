using Partner.Core.Agent;

namespace Partner.Api.Settings;

/// <summary>
/// How big a prompt is, and how big one may be.
/// <para>
/// The local token counter assumes four characters per token, which is right for English and undershoots Chinese
/// badly (a character is closer to one token than a quarter of one). A prompt limit built on it would let a
/// Chinese prompt through at four times the intended size, so this estimate charges a CJK character a whole token.
/// </para>
/// </summary>
public static class PromptSize
{
    /// <summary>A prompt may take at most this share of the prompt budget; the rest is the conversation.</summary>
    public const double MaxShareOfBudget = 0.5;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var wide = 0;
        foreach (var character in text)
        {
            if (character >= 0x2E80)
            {
                wide++;
            }
        }

        return wide + (text.Length - wide + 3) / 4;
    }

    /// <summary>The most tokens one prompt may use under the main model's window.</summary>
    public static int Limit(ModelSettings model)
    {
        // A model section that is itself invalid is reported by its own rules; measuring against it here would
        // throw instead of letting those errors through. The built-in window stands in until it is fixed.
        var usable = model.ContextTokens > 0 && model.MaxTokens > 0 && model.MaxTokens < model.ContextTokens
            ? model
            : new ModelSettings();

        return (int)(ContextBudget.PromptBudget(usable.ContextTokens, usable.MaxTokens) * MaxShareOfBudget);
    }
}
