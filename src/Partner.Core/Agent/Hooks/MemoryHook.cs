using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Puts what earlier sessions knew into this turn's prompt.
/// <para>
/// The read end of the memory wire. Without it the recall pipeline is a library nothing calls: the store
/// fills up and nothing ever looks at it again.
/// </para>
/// <para>
/// Runs through <see cref="IMemoryStore.InjectAsync"/> rather than a bare recall, so long bodies stop at the
/// injection token budget with a truncation marker instead of crowding out the conversation itself.
/// </para>
/// <para>
/// Records distilled from events the prompt still carries are left out. Memory is written at the end of every
/// turn, so the turn that just happened is in the store a second later; recalling it would put the same
/// sentence in the prompt twice, and label the copy "known from earlier sessions" when it is the turn right
/// above. The exclusion is by source event, not by age, so it follows what the assembler actually keeps: once
/// compaction folds that turn into a summary the event leaves the view, and the memory becomes recallable
/// again — which is exactly when it stops being a duplicate.
/// </para>
/// </summary>
public sealed class MemoryRecallHook : AgentHookBase
{
    private readonly IMemoryStore _memory;
    private readonly ISessionStore? _session;

    public MemoryRecallHook(IMemoryStore memory)
        : this(memory, session: null)
    {
    }

    /// <param name="session">
    /// The log the prompt is built from. Optional so a store can be recalled without one — a test, or a caller
    /// that only wants ranked hits — in which case nothing is excluded and the hook behaves as it did before.
    /// </param>
    public MemoryRecallHook(IMemoryStore memory, ISessionStore? session)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _session = session;
    }

    /// <inheritdoc />
    public override AgentPhase Phase => AgentPhase.PreAgentBuild;

    /// <inheritdoc />
    public override string Name => "memory-recall";

    public override async ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        var query = ctx.InputPayload;
        if (string.IsNullOrWhiteSpace(query))
        {
            return HookOutcome.Continue;
        }

        var injection = await _memory
            .InjectAsync(query, await VisibleSeqsAsync(ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        if (injection.Hits.Count == 0)
        {
            return HookOutcome.Continue;
        }

        // Into the system block, not as a user turn: this is context the model should have had, not something
        // the person said. It also keeps the history honest — a remembered fact is not a new message.
        //
        // Each line carries its age, because a memory is not a fact the person just stated. "the user prefers
        // dark mode" from three months ago may since have changed, and the model can only treat it as
        // background if the prompt says it is background. Without this it reads as current.
        var lines = new List<string>(injection.Hits.Count + 3)
        {
            "Known from earlier sessions, most relevant first. Each is background, not something the person just",
            "said: trust it unless they contradict it here, and prefer what they say now over anything below.",
        };

        foreach (var hit in injection.Hits)
        {
            var age = MemoryAge.Describe(hit.CreatedAt, hit.Now);
            var kind = hit.Tier == MemoryTier.Working ? "unconfirmed" : "established";
            lines.Add($"- [{kind}, {age}] {hit.Body}");
        }

        // The budget dropped something the query scored at least as highly as what is here. Saying so stops
        // the prompt from reading complete when it was cut: the model knows there was more than fit.
        if (injection.Truncated)
        {
            lines.Add("(more memories were scored but dropped to stay within the injection token budget)");
        }

        // The label is for the session page, not the model: it names the block in the log the same way a tool
        // card names itself, so the remembered facts read as one thing that happened in the turn rather than
        // as a wall of system text.
        ctx.Inject(string.Join('\n', lines), priority: 50, label: "recall");
        return HookOutcome.Continue;
    }

    /// <summary>
    /// The seqs the prompt still carries, or null when there is no session to ask.
    /// <para>
    /// Every kind counts, not just the conversational ones: the assembler renders tool results and injected
    /// prompts inside the system block, and a memory distilled from a turn is usually a paraphrase of one of
    /// those. Asking "is it in the window at all" is the question that matches what the model reads.
    /// </para>
    /// </summary>
    private async Task<IReadOnlySet<long>?> VisibleSeqsAsync(CancellationToken ct)
    {
        if (_session is null)
        {
            return null;
        }

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);

        // Retraction is already applied by the projection, so what is here is what the next prompt sees.
        return view.Items.Select(item => item.Seq).ToHashSet();
    }
}

/// <summary>
/// Writes down what this turn's exchange was about, so a later turn can recall it.
/// <para>
/// The write end. Runs on <see cref="AgentPhase.Finally"/>, which fires on failure as well as success — and
/// that is right: "the endpoint keeps refusing" is worth remembering too, and it is exactly the kind of fact
/// a person does not want to explain twice.
/// </para>
/// <para>
/// Not a model call. The tier system already answers "what survives": a record lands in
/// <see cref="MemoryTier.Working"/>, is scored on recall, is promoted to an episodic copy only after
/// <c>PromotionAge</c>, and is trimmed if it never matures. A second model call to pre-filter would add latency
/// and a failure mode to solve a problem the scoring already solves — and would make a wrong answer
/// unrecoverable, where a noisy record is one <c>SoftDeleteAsync</c> away.
/// </para>
/// <para>
/// A turn the model spent inside a tool answers with tool results, not prose. The distiller rejects empty
/// text, which would leave desktop-work conclusions (paths chosen, preferences acted on, what was fixed)
/// unrecorded; see <see cref="ToolAppearance.Summarize"/> for the conservative path that covers them.
/// </para>

/// </summary>
public sealed class MemoryDistilHook : AgentHookBase
{
    private readonly IMemoryStore _memory;
    private readonly ISessionStore _session;
    private readonly TimeProvider _clock;

    public MemoryDistilHook(IMemoryStore memory, ISessionStore session, TimeProvider? clock = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override AgentPhase Phase => AgentPhase.Finally;

    /// <inheritdoc />
    public override string Name => "memory-distil";

    public override async ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        // A sub-agent's report is the runtime talking to itself, not something the person said. Distilling it would
        // file "[子 agent … 无法执行 …]" as the person's own statement, and recall would later serve it back as
        // a fact about them.
        if (ctx.Source == InputSource.Agent)
        {
            return HookOutcome.Continue;
        }

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);

        // The last user/assistant exchange in the window: the one this turn produced.
        long? userSeq = null;
        var userText = string.Empty;
        var assistantText = string.Empty;

        foreach (var item in view.Items)
        {
            if (item.Kind == SessionEventKind.User && item.Source == ctx.Source)
            {
                userSeq = item.Seq;
                userText = item.Body;
                assistantText = string.Empty;
            }
            else if (item.Kind == SessionEventKind.Assistant && userSeq is not null)
            {
                assistantText = item.Body;
            }
        }

        if (userSeq is null)
        {
            return HookOutcome.Continue;
        }


        // At-least-once is prevented by asking first. A turn retried after a crash would otherwise record the
        // same fact twice, and recall would return it as two independent pieces of evidence — the one place
        // where a duplicate is not merely redundant but load-bearing on being wrong.
        if (await _memory.FindBySourceAsync(userSeq.Value, ct).ConfigureAwait(false) is not null)
        {
            return HookOutcome.Continue;
        }

        // A turn with prose takes the prose. A turn that was all tools (a desktop task answers "click done",
        // not text) falls back to the most informative tool result of the same exchange — conservatively: only
        // results which say they were executed at a control's centre, which is the store's own success marker.
        var answer = assistantText.Trim();
        if (answer.Length == 0)
        {
            var fromTools = ToolAppearance.Summarize(view.Items, userSeq.Value);
            if (fromTools is null)
            {
                return HookOutcome.Continue;
            }

            answer = fromTools;
        }

        var now = _clock.GetUtcNow();
        var distilled = ConversationDistiller.Distill(userText, answer, userSeq.Value, now);
        if (distilled is null)
        {
            return HookOutcome.Continue;
        }

        await _memory.WriteAsync(
            new MemoryRecord
            {
                // Derived from the source event, so a retry computes the same id and the unique index on
                // record_id stops a duplicate even if the check above were ever removed.
                RecordId = $"m-{userSeq.Value:x}",
                Tier = MemoryTier.Working,
                Scope = MemoryScope.Recall,
                Body = distilled.Body,
                SourceEventSeq = distilled.SourceEventSeq,
                CreatedAt = now,
                Importance = distilled.Importance,
                Veracity = distilled.Veracity,
            },
            ct).ConfigureAwait(false);

        return HookOutcome.Continue;
    }
}
