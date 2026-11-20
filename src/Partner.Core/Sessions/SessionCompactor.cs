using System.Text.Json;
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

/// <summary>Folds older complete turns into one atomic summary checkpoint.</summary>
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

    /// <summary>Summary output allowance for the history budget.</summary>
    public static int SummaryTokensFor(int promptBudget) =>
        Math.Max(1, Math.Min(512, Math.Max(promptBudget / 6, Math.Min(64, promptBudget / 8))));

    /// <summary>History allowance at the 60% post-compaction target, after fixed system costs.</summary>
    public static int HistoryBudget(ContextUsage usage) => Math.Max(1, (int)(usage.BudgetTokens * 0.6) - usage.SystemTokens + usage.SummaryTokens);

    public async Task<CompactionResult> CompactAsync(int promptBudget, CancellationToken ct = default, int? summaryInputBudget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(promptBudget);
        var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
        var conversation = SessionWindowContextAssembler.ReplayableHistory(view.Items)
            .Where(item => item.Kind != SessionEventKind.Summary).ToList();
        var summaries = view.Items.Where(item => item.Kind == SessionEventKind.Summary).ToList();
        var groups = ContextBudget.GroupTurns(conversation);
        if (groups.Count <= 1) return new CompactionResult(0, null, "无需压缩：没有可折叠的旧轮次");

        var summaryTokens = SummaryTokensFor(promptBudget);
        var summaryFraming = await ContextBudget.CostAsync(_tokens, "summary of earlier history: ", ct).ConfigureAwait(false);
        var fits = await ContextBudget.FitNewestGroupsAsync(conversation,
            (entry, token) => ContextBudget.CostAsync(_tokens, SessionWindowContextAssembler.CostText(entry), token),
            Math.Max(0, promptBudget - summaryTokens - summaryFraming), ct).ConfigureAwait(false);
        var keepFrom = fits.Count == 0 ? long.MaxValue : fits[0].Seq;
        // The newest turn is never folded, even when it cannot fit in the current request.
        var older = groups.Take(groups.Count - 1).Where(group => group[0].Seq < keepFrom).ToList();
        if (older.Count == 0) return new CompactionResult(0, null, "无需压缩：历史已在目标预算内");

        var instructions = "Summarise this history for continuing the work. Preserve the goal and constraints, "
            + "decisions and reasons, progress and errors, next steps, and important paths. "
            + "Earlier summaries are rolling context. #seq references are leads to the original log, not verified facts. "
            + $"Reply only with the summary in the same language, at most {summaryTokens} tokens.";
        var available = (summaryInputBudget ?? promptBudget) - summaryTokens
            - await ContextBudget.CostAsync(_tokens, instructions, ct).ConfigureAwait(false)
            - ContextBudget.MessageOverheadTokens;
        var transcript = string.Join("\n", summaries.Select(item => $"#{item.Seq} Summary: {item.Body}"));
        var priorSummaries = transcript;
        if (await _tokens.CountAsync(transcript, ct).ConfigureAwait(false) > available)
            return new CompactionResult(0, null, "压缩失败：摘要请求预算不足");

        var selected = new List<SessionViewItem>();
        foreach (var group in older)
        {
            var rendered = string.Join("\n", group.Select(item => $"#{item.Seq} {item.Kind}: {item.Body}"));
            var separator = transcript.Length == 0 ? "" : "\n";
            var candidate = transcript + separator + rendered;
            if (await _tokens.CountAsync(candidate, ct).ConfigureAwait(false) <= available)
            {
                transcript = candidate;
                selected.AddRange(group);
                continue;
            }
            // A normal-sized next turn belongs to a later pass rather than being needlessly truncated.
            if (await _tokens.CountAsync(priorSummaries + (priorSummaries.Length == 0 ? "" : "\n") + rendered, ct).ConfigureAwait(false) <= available)
                break;

            // A single very large turn can still be represented without changing its original log records.
            const string marker = "\n[... middle of oversized turn omitted ...]\n";
            var low = 0;
            var high = rendered.Length;
            string? excerpt = null;
            while (low <= high)
            {
                var count = (low + high) / 2;
                var first = (count + 1) / 2;
                var last = rendered.Length - count / 2;
                if (first > 0 && first < rendered.Length && char.IsHighSurrogate(rendered[first - 1]) && char.IsLowSurrogate(rendered[first])) first--;
                if (last > 0 && last < rendered.Length && char.IsHighSurrogate(rendered[last - 1]) && char.IsLowSurrogate(rendered[last])) last++;
                var clipped = rendered[..first] + marker + rendered[last..];
                candidate = transcript + separator + clipped;
                if (await _tokens.CountAsync(candidate, ct).ConfigureAwait(false) <= available)
                {
                    excerpt = candidate;
                    low = count + 1;
                }
                else high = count - 1;
            }
            if (excerpt is not null && low > 1)
            {
                transcript = excerpt;
                selected.AddRange(group);
            }
            break;
        }

        if (selected.Count == 0) return new CompactionResult(0, null, "压缩失败：摘要请求预算不足");
        var response = await _llm.CompleteAsync(new LlmRequest(
            new[] { new LlmMessage("system", instructions), new LlmMessage("user", transcript) },
            Array.Empty<ToolSpec>(), MaxTokens: summaryTokens), ct).ConfigureAwait(false);
        var text = (response.Text ?? string.Empty).Trim();
        if (text.Length == 0 || string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            return new CompactionResult(0, null, "压缩失败：摘要为空或被截断");

        var candidates = selected.Concat(summaries).OrderBy(item => item.Seq).ToList();
        var originalCost = 0;
        foreach (var item in candidates)
        {
            var original = item.Kind == SessionEventKind.Summary ? $"summary of earlier history: {item.Body}" : SessionWindowContextAssembler.CostText(item);
            originalCost += await ContextBudget.CostAsync(_tokens, original, ct).ConfigureAwait(false);
        }
        var newCost = await ContextBudget.CostAsync(_tokens, $"summary of earlier history: {text}", ct).ConfigureAwait(false);
        if (newCost >= originalCost) return new CompactionResult(0, null, "压缩失败：摘要没有节省空间");

        var ranges = ContiguousRuns(candidates.Select(item => item.Seq)).Select(range => new[] { range.From, range.To }).ToArray();
        var seq = await _session.AppendIfCurrentAsync(new SessionEvent
        {
            Kind = SessionEventKind.Summary,
            Source = InputSource.Text,
            Body = text,
            Meta = new Dictionary<string, string> { ["compactedRanges"] = JsonSerializer.Serialize(ranges) },
        }, view.LastSeq, ct).ConfigureAwait(false);
        return seq is null
            ? new CompactionResult(0, null, "压缩取消：会话在摘要期间已更改")
            : new CompactionResult(candidates.Count, seq, $"已压缩 {candidates.Count} 条 → #{seq}");
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
