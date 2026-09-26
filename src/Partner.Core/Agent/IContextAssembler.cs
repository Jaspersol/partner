using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Agent;

/// <summary>
/// Builds the messages a turn starts with (design doc §2.9.4):
/// <c>system (base + hook injections)</c> → <c>session window (trimmed)</c> → <c>current input</c>.
/// </summary>
public interface IContextAssembler
{
    Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        CancellationToken ct = default);
}

/// <summary>
/// Default assembler: base system prompt + hook injections + the last <c>windowItems</c> content events
/// of the single session + the current input.
/// <para>
/// Memory injection (design doc §2.5) is a later decorator of this interface — M5 is not built yet, so the
/// budget is enforced here only over the window.
/// </para>
/// </summary>
public sealed class SessionWindowContextAssembler : IContextAssembler
{
    private readonly ISessionStore _session;
    private readonly int _windowItems;
    private readonly string _baseSystemPrompt;

    public SessionWindowContextAssembler(ISessionStore session, int windowItems = 20, string? baseSystemPrompt = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowItems);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _windowItems = windowItems;
        _baseSystemPrompt = baseSystemPrompt ?? "You are a single-user local assistant.";
    }

    public async Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(injectedPrompts);

        var messages = new List<LlmMessage>();

        var system = _baseSystemPrompt;
        if (injectedPrompts.Count > 0)
        {
            system = string.Join(
                "\n\n",
                new[] { system }.Concat(injectedPrompts));
        }

        messages.Add(new LlmMessage("system", system));

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var window = view.Items.Count <= _windowItems
            ? view.Items
            : view.Items.Skip(view.Items.Count - _windowItems).ToList();

        foreach (var entry in window)
        {
            messages.Add(entry.Kind switch
            {
                SessionEventKind.User or SessionEventKind.Schedule => new LlmMessage("user", entry.Body),
                SessionEventKind.Assistant => new LlmMessage("assistant", entry.Body),
                _ => new LlmMessage("system", $"[{entry.Kind}] {entry.Body}"),
            });
        }

        // The current input is the last user message; the window above is history only.
        messages.Add(new LlmMessage("user", item.Payload));
        return messages;
    }
}
