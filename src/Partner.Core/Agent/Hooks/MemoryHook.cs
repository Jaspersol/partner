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
/// </summary>
public sealed class MemoryRecallHook : AgentHookBase
{
    private readonly IMemoryStore _memory;
    private readonly int _recallLimit;

    public MemoryRecallHook(IMemoryStore memory, int recallLimit = 8)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _recallLimit = Math.Max(1, recallLimit);
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

        var hits = await _memory.RecallAsync(query, _recallLimit, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return HookOutcome.Continue;
        }

        // Into the system block, not as a user turn: this is context the model should have had, not something
        // the person said. It also keeps the history honest — a remembered fact is not a new message.
        //
        // Each line carries its age, because a memory is not a fact the person just stated. "the user prefers
        // dark mode" from three months ago may since have changed, and the model can only treat it as
        // background if the prompt says it is background. Without this it reads as current.
        var lines = new List<string>(hits.Count + 2)
        {
            "Known from earlier sessions, most relevant first. Each is background, not something the person just",
            "said: trust it unless they contradict it here, and prefer what they say now over anything below.",
        };

        foreach (var hit in hits)
        {
            var age = MemoryAge.Describe(hit.CreatedAt, hit.Now);
            var kind = hit.Tier == MemoryTier.Working ? "unconfirmed" : "established";
            lines.Add($"- [{kind}, {age}] {hit.Body}");
        }

        ctx.Inject(string.Join('\n', lines), priority: 50);
        return HookOutcome.Continue;
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

        if (userSeq is null || assistantText.Length == 0)
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

        var now = _clock.GetUtcNow();
        var distilled = ConversationDistiller.Distill(userText, assistantText, userSeq.Value, now);
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
