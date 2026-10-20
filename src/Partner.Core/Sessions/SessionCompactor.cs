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

    /// <summary>The summary that was written, so a caller can carry what it says somewhere else (see <see cref="SummaryFacts"/>).</summary>
    public string? SummaryText { get; init; }
}

/// <summary>
/// Folds history that no longer fits the context window into one summary.
/// <para>
/// Both halves are append-only, so folding never loses anything: a <see cref="SessionEventKind.Summary"/>
/// carries the text and a <see cref="SessionEventKind.Retract"/> hides the events it replaced. Without the
/// retraction the summarized events would still be sent and the window would not have shrunk. This is the
/// opposite of an explicit removal, which takes the content out of the log for good.
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
    /// <para>
    /// <paramref name="promptBudget"/> is the room for history: the assembler subtracts the system block (and
    /// the current input) from the window before fitting anything, so passing the whole prompt budget would keep
    /// more than the assembler will send and the next turn would drop it again. Use
    /// <see cref="HistoryBudget"/>.
    /// </para>
    /// </summary>
    /// <summary>How long the summary may be for a window with <paramref name="promptBudget"/> tokens of room: a sixth of it, at most 512 and (for a window big enough to afford it) at least 64.</summary>
    public static int SummaryTokensFor(int promptBudget) =>
        Math.Min(512, Math.Max(promptBudget / 6, Math.Min(64, promptBudget / 8)));

    /// <summary>The room history has once the system block is paid for, never below one token.</summary>
    public static int HistoryBudget(ContextUsage usage) => Math.Max(1, usage.BudgetTokens - usage.SystemTokens);

    public async Task<CompactionResult> CompactAsync(int promptBudget, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(promptBudget);

        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);

        // A summary is context, not a turn: it does not count against the window, so the window keeps
        // bounding the raw conversation. Previous summaries are folded into the new one — otherwise every
        // compaction would add another summary and the context would grow instead of shrinking.
        var conversation = view.Items
            .Where(item => item.Kind != SessionEventKind.Summary && SessionWindowContextAssembler.IsConversational(item))
            .ToList();
        var summaries = view.Items.Where(item => item.Kind == SessionEventKind.Summary).ToList();

        // The summary is about to join the system block, so its room comes out of the history's: fitting against
        // the whole budget and then adding the summary on top would overflow a freshly compacted session. Its
        // length scales with the window instead of being a fixed 512, which is most of a small model's history.
        var summaryTokens = SummaryTokensFor(promptBudget);

        // The same rule the assembler uses: the newest suffix that fits stays, the rest is folded. A
        // disagreement between the two would either send more than the window allows or fold what fits.
        var fits = await ContextBudget.FitNewestFirstAsync(
            conversation,
            (entry, token) => ContextBudget.CostAsync(_tokens, SessionWindowContextAssembler.CostText(entry), token),
            Math.Max(1, promptBudget - summaryTokens),
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
                used += await ContextBudget.CostAsync(_tokens, SessionWindowContextAssembler.CostText(entry), ct).ConfigureAwait(false);
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
                    "Summarise the conversation history below so that someone can carry on from it. Use these labelled "
                    + "lines and leave out a line that has nothing in it: "
                    + "Goal: what the person is trying to get done. "
                    + "Decided: choices already made. "
                    + "Facts: names, numbers, preferences, anything stated that must not be lost. "
                    + "Open: questions still unanswered. "
                    + "In progress: tasks started and not finished. "
                    + "A line that comes from an earlier summary is history too: keep what still matters. "
                    + $"Reply with the summary only, in the same language as the history, in at most {summaryTokens} tokens."),
                new LlmMessage("user", transcript),
            },
            Array.Empty<ToolSpec>(),
            MaxTokens: summaryTokens), ct).ConfigureAwait(false);

        var text = (response.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new CompactionResult(0, null, "压缩失败：模型没有返回摘要");
        }

        // Trimmed only: a summary keeps its line structure, like any other model output.
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

        return new CompactionResult(candidates.Count, summarySeq, $"已压缩 {candidates.Count} 条 → #{summarySeq}（隐藏 {hidden} 条）")
        {
            SummaryText = single,
        };
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
