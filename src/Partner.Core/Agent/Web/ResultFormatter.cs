using System.Globalization;
using System.Text;

namespace Partner.Core.Agent.Web;

/// <summary>
/// The fourth quality gate: saying honestly what the results are.
/// <para>
/// Everything a search returns is a lead, and the model reading this text has no way to tell a fresh result from
/// a stale one, an answer from a served page, or eight sources from one source repeated. So the text says:
/// which results, how many carry a date and where that date came from, how old the whole answer is, which
/// engines answered and which did not with the reason, and — when there is almost nothing — which filter to
/// loosen. Omitting any of that would make the model's confidence a function of the fetch, not of the evidence.
/// </para>
/// </summary>
public sealed class ResultFormatter
{
    /// <summary>At or below this many results, the answer suggests what to relax.</summary>
    public const int SparseThreshold = 3;

    /// <summary>
    /// The sentence that has to be in every answer. It is fixed text rather than a style instruction, because an
    /// instruction can be dropped by a model in a hurry and this one line is the difference between a lead and a
    /// claim.
    /// </summary>
    public const string LeadWarning =
        "搜索结果只是线索，不是答案：摘要是搜索引擎写的，可能过期或断章取义；要下结论请用 web_fetch 打开原文核对。";

    public string Format(
        SearchOutcome outcome,
        int maxResults,
        TimeSpan? cacheAge = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var text = new StringBuilder();
        var hits = outcome.Hits.Take(Math.Clamp(maxResults, 1, 50)).ToList();

        text.AppendLine($"搜索「{outcome.Query.Text}」共 {outcome.Hits.Count} 条结果"
            + (outcome.Hits.Count > hits.Count ? $"，下面列出前 {hits.Count} 条" : string.Empty)
            + "。");
        text.AppendLine();

        if (hits.Count == 0)
        {
            text.AppendLine("没有可用的结果。");
        }
        else
        {
            for (var index = 0; index < hits.Count; index++)
            {
                AppendHit(text, hits[index], index + 1);
            }
        }

        text.AppendLine();
        AppendMetadata(text, outcome, cacheAge);

        if (hits.Count < SparseThreshold)
        {
            text.AppendLine();
            AppendRelaxation(text, outcome);
        }

        text.AppendLine();
        text.Append(LeadWarning);

        return text.ToString();
    }

    private static void AppendHit(StringBuilder text, SearchHit hit, int order)
    {
        text.AppendLine($"{order}. {hit.Title}");

        // The URL goes on its own line, unadorned: it is the one part of this text the model must copy exactly,
        // and it is also what web_fetch will be asked to open.
        text.AppendLine($"   {hit.Url}");

        if (hit.Snippet.Length > 0)
        {
            text.AppendLine($"   {hit.Snippet}");
        }

        var stamps = new List<string> { DescribeDate(hit) };

        if (hit.Sources.Count > 1)
        {
            stamps.Add($"{hit.Sources.Count} 个来源都命中：{string.Join("、", hit.Sources)}");
        }
        else if (hit.Sources.Count == 1)
        {
            stamps.Add($"来源：{hit.Sources[0]}");
        }

        text.AppendLine($"   [{string.Join(" | ", stamps)}]");
    }

    /// <summary>
    /// A date is printed with its provenance. "日期不详" is not a gap to be filled in by a guess — it means the
    /// search index did not state one, and a result without a date may be from any year.
    /// </summary>
    private static string DescribeDate(SearchHit hit) => hit.Published is { } published
        ? hit.DateSource switch
        {
            DateSource.Structured => $"日期 {published:yyyy-MM-dd}（结构化字段）",
            DateSource.Relative => $"日期 {published:yyyy-MM-dd}（引擎标注的相对时间）",
            DateSource.Text => $"日期 {published:yyyy-MM-dd}（正文中的日期，仅供参考）",
            _ => $"日期 {published:yyyy-MM-dd}",
        }
        : "日期不详（年龄未知）";

    private static void AppendMetadata(StringBuilder text, SearchOutcome outcome, TimeSpan? cacheAge)
    {
        var hits = outcome.Hits;
        var dated = hits.Count(hit => hit.Published is not null);

        var bySource = hits
            .Where(hit => hit.Published is not null)
            .GroupBy(hit => hit.DateSource)
            .ToDictionary(group => group.Key, group => group.Count());

        text.AppendLine("—— 本次检索 ——");
        text.AppendLine($"查询：「{outcome.Query.Text}」，kind={outcome.Query.Kind}"
            + (outcome.Query.Recency is { } recency ? $"，recency={recency}" : string.Empty));

        text.AppendLine($"结果 {hits.Count} 条，其中有日期 {dated} 条"
            + $"（结构化 {Count(DateSource.Structured)}、相对 {Count(DateSource.Relative)}、"
            + $"正文 {Count(DateSource.Text)}）；其余 {hits.Count - dated} 条日期不详，不代表新，只代表不知道。");

        text.AppendLine(cacheAge is { } age
            ? $"时效：这份结果取自 {Describe(age)}前的缓存，不是刚刚查的。"
            : $"时效：刚刚检索完成，用时 {outcome.Elapsed.TotalSeconds:0.0} 秒"
                + (outcome.BudgetExpired ? "（到达时间预算，未返回的引擎已取消）" : string.Empty) + "。");

        var answered = outcome.Attempts.Where(attempt => attempt.Answered).ToList();
        text.AppendLine(answered.Count == 0
            ? "答了：没有引擎返回结果。"
            : "答了：" + string.Join("、", answered.Select(attempt => $"{attempt.Engine.Traits.Name} {attempt.Hits.Count} 条")));

        var silent = outcome.Attempts.Where(attempt => !attempt.Answered).ToList();
        if (silent.Count > 0)
        {
            text.AppendLine("没答：" + string.Join("；", silent.Select(attempt => $"{attempt.Engine.Traits.Name}（{attempt.Reason}）")));
        }

        var dropped = outcome.Batches.Where(batch => !batch.Keep && batch.Rejection is "decoy").ToList();
        if (dropped.Count > 0)
        {
            text.AppendLine("被丢弃："
                + string.Join("、", dropped.Select(batch =>
                    $"{batch.Engine.Traits.Name} 的一整批结果与查询词几乎无关（可能是给爬虫看的页面），按诱饵丢弃"))
                + "。");
        }

        if (outcome.Skipped.Count > 0)
        {
            text.AppendLine("未问：" + string.Join("；", outcome.Skipped));
        }

        int Count(DateSource source) => bySource.TryGetValue(source, out var value) ? value : 0;
    }

    /// <summary>
    /// What to try next when there is almost nothing. The three levers are the three the caller controls, and
    /// they are named in the order that is most likely to be the culprit: a recency filter discards results that
    /// were found, a narrow kind never asked anybody who indexes widely, and the keywords are the last resort.
    /// </summary>
    private static void AppendRelaxation(StringBuilder text, SearchOutcome outcome)
    {
        var suggestions = new List<string>();

        if (outcome.Query.Recency is not null)
        {
            suggestions.Add($"去掉 recency={outcome.Query.Recency}（它只按结构化/相对日期过滤，会丢掉日期不详的结果）");
        }

        if (outcome.Query.Kind != SearchKinds.General)
        {
            suggestions.Add($"换成 kind=general（{outcome.Query.Kind} 只问专门索引该类型的源）");
        }

        suggestions.Add("换一组关键词（同义说法、英文原名、去掉修饰词）");

        text.AppendLine($"结果偏少（少于 {SparseThreshold} 条），可以试试：{string.Join("；", suggestions)}。");
    }

    private static string Describe(TimeSpan age) => age.TotalMinutes < 1
        ? $"{Math.Max(1, (int)age.TotalSeconds)} 秒"
        : age.TotalHours < 1
            ? $"{(int)age.TotalMinutes} 分钟"
            : $"{age.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} 小时";
}
