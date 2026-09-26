using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Agent;

/// <summary>
/// Builds the messages a turn starts with (design doc §2.9.4):
/// <c>system (base + hook injections)</c> → <c>session window (trimmed to the token budget)</c> →
/// <c>current input</c>.
/// </summary>
public interface IContextAssembler
{
    Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        CancellationToken ct = default);
}

/// <summary>
/// Default assembler: base system prompt + hook injections + the newest history that fits the model's context
/// window + the current input.
/// <para>
/// The window is a <b>token</b> budget, not an item count: what overflows a model is tokens, and the two are
/// only loosely related (one tool result can outweigh twenty short turns). The budget reserves room for the
/// completion, so a prompt can never fill the window and leave the answer truncated.
/// </para>
/// <para>
/// Memory injection (design doc §2.5) is a later decorator of this interface.
/// </para>
/// </summary>
public sealed class SessionWindowContextAssembler : IContextAssembler
{
    private readonly ISessionStore _session;
    private readonly int _contextTokens;
    private readonly int _reserveForOutput;
    private readonly ITokenCounter _tokens;
    private readonly string _baseSystemPrompt;

    public SessionWindowContextAssembler(
        ISessionStore session,
        int contextTokens = 32768,
        int reserveForOutput = 4096,
        ITokenCounter? tokens = null,
        string? baseSystemPrompt = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);

        _session = session ?? throw new ArgumentNullException(nameof(session));
        _contextTokens = contextTokens;
        _reserveForOutput = reserveForOutput;
        _tokens = tokens ?? new ApproximateTokenCounter();
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

        // A summary stands in for history that was folded away, so it is context rather than a turn.
        var summaries = view.Items.Where(entry => entry.Kind == SessionEventKind.Summary).ToList();
        var conversation = view.Items.Where(entry => entry.Kind != SessionEventKind.Summary).ToList();

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

        // The system block is always sent: dropping it would remove the instructions the turn runs under.
        var systemTokens = ContextBudget.Cost(_tokens, _baseSystemPrompt)
            + SumTokens(injectedPrompts)
            + context.Sum(entry => ContextBudget.Cost(_tokens, entry));

        var budget = ContextBudget.PromptBudget(_contextTokens, _reserveForOutput)
            - systemTokens
            - ContextBudget.Cost(_tokens, item.Payload);

        var kept = ContextBudget.FitNewestFirst(
            conversation,
            entry => ContextBudget.Cost(_tokens, entry.Body),
            Math.Max(0, budget));

        // Entries that are not conversational are still shown to the model, just inside the system block.
        foreach (var entry in kept.Where(entry => entry.Kind is not (SessionEventKind.User or SessionEventKind.Assistant or SessionEventKind.Schedule)))
        {
            context.Add($"#{entry.Seq} [{entry.Kind}] {entry.Body}");
        }

        var turns = kept
            .Where(entry => entry.Kind is SessionEventKind.User or SessionEventKind.Assistant or SessionEventKind.Schedule)
            .Select(entry => entry.Kind == SessionEventKind.Assistant
                ? new LlmMessage("assistant", entry.Body)
                : new LlmMessage("user", entry.Body))
            .ToList();

        var messages = new List<LlmMessage> { new("system", BuildSystem(_baseSystemPrompt, injectedPrompts, context)) };
        messages.AddRange(turns);

        // The current input is the last user message; the window above is history only.
        messages.Add(new LlmMessage("user", item.Payload));
        return messages;
    }

    /// <summary>Cost of the injected prompts, which are always sent.</summary>
    private int SumTokens(IReadOnlyList<string> prompts)
    {
        var total = 0;
        foreach (var prompt in prompts)
        {
            total += ContextBudget.Cost(_tokens, prompt);
        }

        return total;
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


