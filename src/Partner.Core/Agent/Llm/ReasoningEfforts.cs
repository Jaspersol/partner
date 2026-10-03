namespace Partner.Core.Agent.Llm;

/// <summary>
/// The thinking gear a model may be asked for: how much reasoning to spend before answering.
/// <para>
/// The values are the ones providers converge on (<c>reasoning_effort</c> in the OpenAI-compatible shape),
/// but they are <b>not</b> clamped to that list: endpoints invent their own (<c>minimal</c>, <c>none</c>,
/// provider-specific words), and refusing a value the server would have understood is worse than passing it
/// through. What is normalized is only the shape — trimmed, lower case — and <see cref="Off"/>, which means
/// "send nothing" rather than a value: a model that does not take the parameter must not be sent a guess at
/// one.
/// </para>
/// </summary>
public static class ReasoningEfforts
{
    /// <summary>Do not put the parameter on the wire at all.</summary>
    public const string Off = "off";

    /// <summary>What a configuration that never mentions the gear sends.</summary>
    public const string Default = "medium";

    /// <summary>The gears the UI offers, in order. Any other value may still be typed in.</summary>
    public static IReadOnlyList<string> Values { get; } = new[] { Off, "low", Default, "high", "xhigh", "max" };

    /// <summary>Trimmed and lower case, so <c>HIGH</c> and <c>high </c> are one setting.</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// What to send as <c>reasoning_effort</c>, or null when the field must not be sent: off, or a value that
    /// is blank (an emptied field means "say nothing about it").
    /// </summary>
    public static string? Wire(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 || normalized == Off ? null : normalized;
    }
}
