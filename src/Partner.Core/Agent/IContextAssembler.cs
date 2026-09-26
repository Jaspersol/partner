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

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);

        // A summary stands in for history that was folded away, so it is context rather than a turn: it does
        // not consume the window, which keeps bounding the raw conversation.
        var summaries = view.Items.Where(item => item.Kind == SessionEventKind.Summary).ToList();
        var conversation = view.Items.Where(item => item.Kind != SessionEventKind.Summary).ToList();
        var window = conversation.Count <= _windowItems
            ? conversation
            : conversation.Skip(conversation.Count - _windowItems).ToList();

        // A chat-completions conversation has exactly one legal shape: system content first, then the
        // user/assistant turns. Window entries that are neither (tool results, hook injections) must therefore
        // be folded into the leading system message rather than emitted in place — sending a `system` message
        // after a `user` message is rejected outright by providers (llama.cpp's templates raise
        // "System message must be at the beginning").
        var context = new List<string>();

        foreach (var summary in summaries)
        {
            context.Add($"summary of earlier history: {summary.Body}");
        }

        var turns = new List<LlmMessage>();

        foreach (var entry in window)
        {
            switch (entry.Kind)
            {
                case SessionEventKind.User or SessionEventKind.Schedule:
                    turns.Add(new LlmMessage("user", entry.Body));
                    break;

                case SessionEventKind.Assistant:
                    turns.Add(new LlmMessage("assistant", entry.Body));
                    break;

                default:
                    // Keeps the seq so the model can be told which entry it is looking at, and so the block
                    // stays auditable against the raw log.
                    context.Add($"#{entry.Seq} [{entry.Kind}] {entry.Body}");
                    break;
            }
        }

        var messages = new List<LlmMessage> { new("system", BuildSystem(_baseSystemPrompt, injectedPrompts, context)) };
        messages.AddRange(turns);

        // The current input is the last user message; the window above is history only.
        messages.Add(new LlmMessage("user", item.Payload));
        return messages;
    }

    private static string BuildSystem(string basePrompt, IReadOnlyList<string> injectedPrompts, IReadOnlyList<string> context)
    {
        var blocks = new List<string> { basePrompt };
        blocks.AddRange(injectedPrompts);

        if (context.Count > 0)
        {
            blocks.Add("Earlier activity in this session:\n" + string.Join("\n", context));
        }

        return string.Join("\n\n", blocks);
    }
}
