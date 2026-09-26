using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Agent;

/// <summary>How much of the context window the history currently occupies.</summary>
public sealed record ContextUsage(
    int UsedTokens,
    int BudgetTokens,
    int ContextTokens,
    int MaxTokens,
    int KeptItems,
    int FoldedItems)
{
    public double Fraction => BudgetTokens <= 0 ? 0 : (double)UsedTokens / BudgetTokens;
}

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

    /// <summary>What the history costs right now, without a turn — the number a UI shows.</summary>
    Task<ContextUsage> MeasureAsync(CancellationToken ct = default);
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
/// Counting goes through <see cref="ITokenCounter.CountAsync"/>, so a provider-backed counter is used where
/// one exists and the local estimate is the fallback.
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

    public async Task<ContextUsage> MeasureAsync(CancellationToken ct = default)
    {
        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var plan = await PlanAsync(view, injectedPrompts: Array.Empty<string>(), currentInput: null, ct)
            .ConfigureAwait(false);

        return new ContextUsage(
            plan.SystemTokens + plan.TurnTokens,
            ContextBudget.PromptBudget(_contextTokens, _reserveForOutput),
            _contextTokens,
            _reserveForOutput,
            plan.Kept.Count,
            plan.FoldedCount);
    }

    public async Task<IReadOnlyList<LlmMessage>> AssembleInitialAsync(
        InputItem item,
        IReadOnlyList<string> injectedPrompts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(injectedPrompts);

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var plan = await PlanAsync(view, injectedPrompts, item.Payload, ct).ConfigureAwait(false);

        var messages = new List<LlmMessage>
        {
            new("system", BuildSystem(_baseSystemPrompt, injectedPrompts, plan.Context)),
        };

        messages.AddRange(plan.Turns);

        // The current input is the last user message; the window above is history only.
        messages.Add(new LlmMessage("user", item.Payload));
        return messages;
    }

    /// <summary>Everything the two public entry points share: what fits, and what it costs.</summary>
    private async Task<Plan> PlanAsync(
        SessionView view,
        IReadOnlyList<string> injectedPrompts,
        string? currentInput,
        CancellationToken ct)
    {
        // A summary stands in for history that was folded away, so it is context rather than a turn.
        var summaries = view.Items.Where(entry => entry.Kind == SessionEventKind.Summary).ToList();
        var conversation = view.Items.Where(entry => entry.Kind != SessionEventKind.Summary).ToList();

        var context = summaries.Select(summary => $"summary of earlier history: {summary.Body}").ToList();

        // The system block is always sent: dropping it would remove the instructions the turn runs under.
        var systemTokens = await ContextBudget.CostAsync(_tokens, _baseSystemPrompt, ct).ConfigureAwait(false);

        foreach (var prompt in injectedPrompts)
        {
            systemTokens += await ContextBudget.CostAsync(_tokens, prompt, ct).ConfigureAwait(false);
        }

        foreach (var entry in context)
        {
            systemTokens += await ContextBudget.CostAsync(_tokens, entry, ct).ConfigureAwait(false);
        }

        var inputTokens = currentInput is null
            ? 0
            : await ContextBudget.CostAsync(_tokens, currentInput, ct).ConfigureAwait(false);

        var budget = ContextBudget.PromptBudget(_contextTokens, _reserveForOutput) - systemTokens - inputTokens;

        var kept = await ContextBudget.FitNewestFirstAsync(
            conversation,
            (entry, token) => ContextBudget.CostAsync(_tokens, entry.Body, token),
            Math.Max(0, budget),
            ct).ConfigureAwait(false);

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

        var turnTokens = 0;
        foreach (var entry in kept)
        {
            turnTokens += await ContextBudget.CostAsync(_tokens, entry.Body, ct).ConfigureAwait(false);
        }

        return new Plan(
            context,
            turns,
            kept,
            systemTokens,
            turnTokens,
            conversation.Count - kept.Count);
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

    private sealed record Plan(
        List<string> Context,
        List<LlmMessage> Turns,
        List<SessionViewItem> Kept,
        int SystemTokens,
        int TurnTokens,
        int FoldedCount);
}
