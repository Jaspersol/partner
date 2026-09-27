using System.Globalization;
using Partner.Core.Agent;
using Partner.Core.Agent.Llm;
using Partner.Core.Memory;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;

namespace Partner.Core.Sessions;

/// <summary>What one compaction pass did.</summary>
public sealed record CompactionResult(int FoldedEvents, long? SummarySeq, string Reason)
{
    public bool Compacted => SummarySeq is not null;
}

/// <summary>
/// Folds history that no longer fits the context window into one summary (design doc §2.5 short-term
/// memory: "窗口内上下文 + 压缩").
/// <para>
/// Both halves are append-only, so folding never loses anything: a <see cref="SessionEventKind.Summary"/>
/// carries the text and a <see cref="SessionEventKind.Retract"/> hides the events it replaced. Without the
/// retraction the summarized events would still be sent, and the window would not have shrunk at all. This is
/// the deliberate opposite of an explicit removal, which takes the content out of the log for good.
/// </para>
/// </summary>
public sealed class SessionCompactor
{
    private readonly ISessionStore _session;
    private readonly ILlmClient _llm;
    private readonly ITokenCounter _tokens;

    public SessionCompactor(ISessionStore session, ILlmClient llm, ITokenCounter? tokens = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _tokens = tokens ?? new ApproximateTokenCounter();
    }

    /// <summary>
    /// Summarises the history that no longer fits <paramref name="promptBudget"/> tokens. Does nothing when
    /// the history already fits, so calling it after every settings change is safe.
    /// </summary>
    public async Task<CompactionResult> CompactAsync(int promptBudget, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(promptBudget);

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);

        // A summary is context, not a turn: it does not count against the window, so the window keeps
        // bounding the raw conversation. Previous summaries are folded into the new one — otherwise every
        // compaction would add another summary and the context would grow instead of shrinking.
        var conversation = view.Items
            .Where(item => item.Kind != SessionEventKind.Summary && item.Kind.IsContent())
            .ToList();
        var summaries = view.Items.Where(item => item.Kind == SessionEventKind.Summary).ToList();

        // The same rule the assembler uses: the newest suffix that fits stays, the rest is folded. A
        // disagreement between the two would either send more than the window allows or fold what fits.
        var fits = await ContextBudget.FitNewestFirstAsync(
            conversation,
            (entry, token) => ContextBudget.CostAsync(_tokens, entry.Body, token),
            promptBudget,
            ct).ConfigureAwait(false);

        var keepFrom = fits.Count > 0 ? fits[0].Seq : long.MaxValue;
        var older = conversation.Where(item => item.Seq < keepFrom).ToList();

        // Nothing to fold means the history fits, and re-summarising an existing summary would spend a model
        // call to make the context longer rather than shorter.
        if (older.Count == 0)
        {
            var used = 0;
            foreach (var entry in fits)
            {
                used += await ContextBudget.CostAsync(_tokens, entry.Body, ct).ConfigureAwait(false);
            }
            return new CompactionResult(0, null, $"无需压缩：历史约 {used} token ≤ 预算 {promptBudget}");
        }

        // A previous summary describes history that is now even older, so it is folded in rather than kept:
        // otherwise every pass would add context instead of removing it.
        var candidates = older.Concat(summaries).OrderBy(item => item.Seq).ToList();

        if (candidates.Count == 0)
        {
            return new CompactionResult(0, null, "无需压缩：窗口外没有可摘要的内容");
        }

        var transcript = string.Join(
            "\n",
            candidates.Select(item => $"{item.Kind}: {item.Body}"));

        var response = await _llm.CompleteAsync(new LlmRequest(
            new[]
            {
                new LlmMessage(
                    "system",
                    "Summarise the conversation history below. Keep decisions, facts, names and open questions. "
                    + "Reply with the summary only, in the same language as the history, and in a single paragraph."),
                new LlmMessage("user", transcript),
            },
            Array.Empty<ToolSpec>(),
            MaxTokens: 512), ct).ConfigureAwait(false);

        var text = (response.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new CompactionResult(0, null, "压缩失败：模型没有返回摘要");
        }

        // Only trimmed: a summary keeps its line structure, like any other model output.
        var single = text.Trim();

        var summarySeq = await _session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Summary,
            Source = InputSource.Text,
            Body = single,
            Meta = new Dictionary<string, string>
            {
                ["folded"] = candidates.Count.ToString(CultureInfo.InvariantCulture),
                ["coveredFrom"] = candidates[0].Seq.ToString(CultureInfo.InvariantCulture),
                ["coveredTo"] = candidates[candidates.Count - 1].Seq.ToString(CultureInfo.InvariantCulture),
            },
        }, ct).ConfigureAwait(false);

        // One retraction per contiguous run: the candidates are usually adjacent, but a previous summary sits
        // after the turns it covers, so the covered seqs are not necessarily one range.
        var hidden = 0;
        foreach (var (from, to) in ContiguousRuns(candidates.Select(item => item.Seq)))
        {
            await _session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Retract,
                Source = InputSource.Text,
                Body = $"compacted #{from}..{to} into #{summarySeq}",
                Meta = new Dictionary<string, string>
                {
                    [SessionRetraction.FromKey] = from.ToString(CultureInfo.InvariantCulture),
                    [SessionRetraction.ToKey] = to.ToString(CultureInfo.InvariantCulture),
                },
            }, ct).ConfigureAwait(false);

            hidden += (int)(to - from + 1);
        }

        return new CompactionResult(candidates.Count, summarySeq, $"已压缩 {candidates.Count} 条 → #{summarySeq}（隐藏 {hidden} 条）");
    }

    /// <summary>Groups sorted seqs into the ranges a retraction can express.</summary>
    private static IEnumerable<(long From, long To)> ContiguousRuns(IEnumerable<long> seqs)
    {
        var ordered = seqs.Distinct().OrderBy(seq => seq).ToList();
        if (ordered.Count == 0)
        {
            yield break;
        }

        var from = ordered[0];
        var previous = ordered[0];

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i] != previous + 1)
            {
                yield return (from, previous);
                from = ordered[i];
            }

            previous = ordered[i];
        }

        yield return (from, previous);
    }
}
