using System.Text.Json;
using Partner.Core.Contracts;

namespace Partner.Core.Agent.Delegation;

public sealed partial class JobEngine
{
    /// <summary>What the log says one round did, read back after the round rather than trusted from the model.</summary>
    /// <param name="Actions">The calls made, most frequent first, as <c>name ×n</c> with the action kinds when the tool has them.</param>
    /// <param name="Calls">The same, as counts by tool name, for the job-wide total.</param>
    /// <param name="LastFailure">The last tool failure text, or null if no tool failed.</param>
    /// <param name="Terminal">A <see cref="JobCategories"/> value when a tool failure means no round can succeed.</param>
    /// <param name="StepLimit">Whether the round ended because its step budget was spent.</param>
    private sealed record RoundFacts(
        IReadOnlyList<string> Actions,
        IReadOnlyDictionary<string, int> Calls,
        IReadOnlyDictionary<string, Dictionary<string, int>> Kinds,
        string? LastFailure,
        string? Terminal,
        bool StepLimit);

    private async Task<RoundFacts> ReadRoundAsync(long before, CancellationToken ct)
    {
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        string? lastFailure = null;
        string? terminal = null;
        var stepLimit = false;

        await foreach (var logged in session.ReadAsync(before + 1, 100_000, ct).ConfigureAwait(false))
        {
            switch (logged.Kind)
            {
                case SessionEventKind.ToolCall when logged.Meta is not null && logged.Meta.TryGetValue("name", out var name):
                    calls[name] = calls.GetValueOrDefault(name) + 1;
                    if (KindOf(logged.Meta.GetValueOrDefault("args")) is { } kind)
                    {
                        var perKind = kinds.TryGetValue(name, out var existing) ? existing : kinds[name] = new(StringComparer.Ordinal);
                        perKind[kind] = perKind.GetValueOrDefault(kind) + 1;
                    }

                    break;

                case SessionEventKind.ToolResult when logged.Meta is not null
                    && logged.Meta.TryGetValue("ok", out var ok) && ok == "false":
                    lastFailure = logged.Body;
                    terminal = Stronger(terminal, TerminalOf(logged.Body));
                    break;

                case SessionEventKind.TurnEnd when logged.Meta is not null
                    && logged.Meta.TryGetValue("detail", out var detail) && detail == "step-limit":
                    stepLimit = true;
                    break;
            }
        }

        return new RoundFacts(
            Describe(calls, kinds), calls, kinds, lastFailure is null ? null : Truncate(lastFailure), terminal, stepLimit);
    }

    /// <summary>Tool calls as <c>name ×n</c>, with the action kinds in brackets when the tool carries them.</summary>
    private static IReadOnlyList<string> Describe(
        IReadOnlyDictionary<string, int> calls,
        IReadOnlyDictionary<string, Dictionary<string, int>> kinds) =>
        calls.OrderByDescending(pair => pair.Value).Select(pair =>
            kinds.TryGetValue(pair.Key, out var perKind)
                ? $"{pair.Key} ×{pair.Value}（{string.Join("、", perKind.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}）"
                : $"{pair.Key} ×{pair.Value}").ToList();

    /// <summary>
    /// Whether a tool failure is a refusal rather than a miss. The prefixes are the ones the runner and the
    /// file tools write; the host adds markers for failures only it knows how to recognise (an elevated window).
    /// </summary>
    private string? TerminalOf(string body)
    {
        var text = body.TrimStart();
        if (text.StartsWith("approval denied:", StringComparison.Ordinal))
        {
            return JobCategories.ApprovalDenied;
        }

        if (text.StartsWith("denied:", StringComparison.Ordinal)
            || text.StartsWith("outside_roots:", StringComparison.Ordinal)
            || text.StartsWith("denied_file:", StringComparison.Ordinal))
        {
            return JobCategories.GovernanceDenied;
        }

        return extraTerminal?.Invoke(body);
    }

    /// <summary>An unanswered approval outranks a rule refusal: it names the person, which is who the main agent should tell.</summary>
    private static string? Stronger(string? current, string? candidate) =>
        current == JobCategories.ApprovalDenied ? current : candidate ?? current;

    private static string? KindOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                    ? kind.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
