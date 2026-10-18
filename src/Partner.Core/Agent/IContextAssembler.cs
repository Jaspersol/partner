using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Media;
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
    private readonly Func<string?>? _promptSource;
    private readonly IMediaStore? _media;

    public SessionWindowContextAssembler(
        ISessionStore session,
        int contextTokens = 32768,
        int reserveForOutput = 4096,
        ITokenCounter? tokens = null,
        string? baseSystemPrompt = null,
        IMediaStore? media = null,
        Func<string?>? promptSource = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);

        _session = session ?? throw new ArgumentNullException(nameof(session));
        _contextTokens = contextTokens;
        _reserveForOutput = reserveForOutput;
        _tokens = tokens ?? new ApproximateTokenCounter();
        _baseSystemPrompt = baseSystemPrompt ?? AgentPrompts.MainDefault;
        _promptSource = promptSource;
        _media = media;
    }

    /// <summary>
    /// The prompt in force right now: the live source when there is one and it has something to say, else the
    /// fixed one. Read per call, never cached — that is the entire point of the source.
    /// </summary>
    private string BasePrompt =>
        _promptSource?.Invoke() is { } live && !string.IsNullOrWhiteSpace(live) ? live : _baseSystemPrompt;

    public async Task<ContextUsage> MeasureAsync(CancellationToken ct = default)
    {
        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var plan = await PlanAsync(view, injectedPrompts: Array.Empty<string>(), currentInput: null, fromMemory: true, ct)
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
        var attachments = ResolveAttachments(item.MediaIds);
        var images = attachments.Where(a => a.IsImage).Select(a => a.ToDataUri()).ToList();
        var plan = await PlanAsync(view, injectedPrompts, item.Payload, fromMemory: false, ct, images.Count)
            .ConfigureAwait(false);

        var messages = new List<LlmMessage>
        {
            new("system", BuildSystem(BasePrompt, injectedPrompts, plan.Context)),
        };

        messages.AddRange(plan.Turns);

        // The current input is the last user message; the window above is history only.
        messages.Add(new LlmMessage(
            "user",
            MessageBody(item.Payload, attachments),
            Images: images.Count > 0 ? images : null));
        return messages;
    }

    /// <summary>
    /// The text of the current input, followed by where its non-image attachments are.
    ///
    /// <para>
    /// A document cannot ride an <c>image_url</c> part — that does not degrade, it fails the whole request — so
    /// it travels as a path instead. That is also the form a tool like <c>read</c> can act on: the model has to
    /// be able to open the file, not merely be told that it exists.
    /// </para>
    /// </summary>
    private static string MessageBody(string payload, IReadOnlyList<MediaAttachment> attachments)
    {
        var documents = attachments.Where(a => !a.IsImage).ToList();
        if (documents.Count == 0)
        {
            return payload;
        }

        var lines = new List<string> { payload };
        lines.Add(string.Empty);
        lines.Add($"（随本条消息附带 {documents.Count} 个文件，已保存在本地，可直接按路径读取）:");

        foreach (var document in documents)
        {
            lines.Add($"- {document.FileName} → {document.LocalPath}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The attachments of the input being sent. Only this input's: history stays textual, so a later question
    /// does not re-send megabytes the model has already seen — and does not quote paths that may since have been
    /// deleted.
    /// </summary>
    private IReadOnlyList<MediaAttachment> ResolveAttachments(IReadOnlyList<string> mediaIds)
    {
        if (_media is null || mediaIds.Count == 0)
        {
            return Array.Empty<MediaAttachment>();
        }

        var resolved = new List<MediaAttachment>();
        foreach (var id in mediaIds)
        {
            if (_media.TryRead(id) is { } attachment)
            {
                resolved.Add(attachment);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Everything the two public entry points share: what fits, and what it costs.
    /// <para>
    /// <paramref name="fromMemory"/> is how the read-only measurement stays read-only: a turn may ask the model
    /// for a token count, but reporting occupancy must not, because the session view would then wait on — or
    /// fail because of — a model endpoint that is slow, dead, or not configured yet.
    /// </para>
    /// </summary>
    private async Task<Plan> PlanAsync(
        SessionView view,
        IReadOnlyList<string> injectedPrompts,
        string? currentInput,
        bool fromMemory,
        CancellationToken ct,
        int imageCount = 0)
    {
        // A summary stands in for history that was folded away, so it is context rather than a turn.
        var summaries = view.Items.Where(entry => entry.Kind == SessionEventKind.Summary).ToList();
        var conversation = view.Items
            .Where(entry => entry.Kind != SessionEventKind.Summary && entry.Kind.IsContent())
            .ToList();

        ValueTask<int> Cost(string? text, CancellationToken token) => fromMemory
            ? ValueTask.FromResult(_tokens.Count(text ?? string.Empty) + ContextBudget.MessageOverheadTokens)
            : ContextBudget.CostAsync(_tokens, text, token);

        var context = summaries.Select(summary => $"summary of earlier history: {summary.Body}").ToList();

        // The system block is always sent: dropping it would remove the instructions the turn runs under.
        var systemTokens = await Cost(BasePrompt, ct).ConfigureAwait(false);

        foreach (var prompt in injectedPrompts)
        {
            systemTokens += await Cost(prompt, ct).ConfigureAwait(false);
        }

        foreach (var entry in context)
        {
            systemTokens += await Cost(entry, ct).ConfigureAwait(false);
        }

        var inputTokens = currentInput is null
            ? 0
            : await Cost(currentInput, ct).ConfigureAwait(false);

        // An attachment costs the window something a character count cannot see. The number is a conservative
        // flat charge per image rather than a model-specific measurement: it keeps the budget from claiming
        // room that the image will actually take.
        inputTokens += imageCount * ContextBudget.ImageTokens;

        var budget = ContextBudget.PromptBudget(_contextTokens, _reserveForOutput) - systemTokens - inputTokens;

        var kept = await ContextBudget.FitNewestFirstAsync(
            conversation,
            (entry, token) => Cost(entry.Body, token),
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
            turnTokens += await Cost(entry.Body, ct).ConfigureAwait(false);
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
