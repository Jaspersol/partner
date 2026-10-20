using Partner.Core.Contracts;

namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Hook runtime context: one instance per turn, passed between hooks across phases.
/// <para>
/// Not thread-safe: owned by the turn's executing thread; the ordering cache for <see cref="InjectedPrompts"/>
/// is built on first read.
/// </para>
/// </summary>
public sealed class HookContext
{
    private readonly List<(int Priority, string Text, string? Label)> _injected = new();
    private IReadOnlyList<string>? _orderedInjected;

    /// <summary>The session this turn belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>The source of the input that triggered this turn.</summary>
    public required InputSource Source { get; init; }

    /// <summary>Raw input payload (speech transcript, user text, or scheduled event payload).</summary>
    public required string InputPayload { get; init; }

    /// <summary>Working directory for this turn.</summary>
    public required string WorkspaceDirectory { get; init; }

    /// <summary>Prompts injected by hooks, ordered by priority (ascending) then insertion order.</summary>
    public IReadOnlyList<string> InjectedPrompts =>
        // LINQ OrderBy is a stable sort: equal priorities keep insertion order.
        _orderedInjected ??= _injected.OrderBy(e => e.Priority).Select(e => e.Text).ToArray();

    /// <summary>
    /// Labels corresponding one-to-one with <see cref="InjectedPrompts"/>; null where there is no label.
    /// <para>
    /// A separate channel, not folded into the text: labels are for people, text is for the model, and mixing
    /// them would show the model words that belong only to the UI.
    /// </para>
    /// </summary>
    public IReadOnlyList<string?> InjectedLabels =>
        _injected.OrderBy(e => e.Priority).Select(e => e.Label).ToArray();

    /// <summary>Namespace dictionary for passing data between hooks (snapshots and the like).</summary>
    public IDictionary<string, string> Extras { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Error message; written by hooks in the <see cref="AgentPhase.OnError"/> phase.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Injects a prompt. A smaller <paramref name="priority"/> comes first; equal priorities keep insertion order.
    /// </summary>
    /// <param name="label">
    /// What this text is, for people reading the session (for example <c>recall</c>). Display only: the prompt
    /// itself is unchanged, and the model still sees <paramref name="text"/>. When omitted, the session page
    /// treats it as an ordinary system line.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null, empty, or whitespace.</exception>
    public void Inject(string text, int priority = 100, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _injected.Add((priority, text, label));
        _orderedInjected = null;
    }
}
