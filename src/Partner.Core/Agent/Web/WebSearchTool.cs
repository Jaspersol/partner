using System.Text.Json;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Web;

/// <summary>
/// <c>web_search</c>: one question, several engines, one merged list of leads.
/// <para>
/// The description is part of the mechanism, not decoration. A model that reads a list of titles with snippets
/// will treat it as an answer unless it is told otherwise, so the text it is registered with says what the
/// result actually is — leads, possibly stale, possibly missing a date — and says that <c>web_fetch</c> is how to
/// check one. The formatter repeats the same warning in the result itself, because a description is read once and
/// the result is read every time.
/// </para>
/// </summary>
public sealed class WebSearchTool(WebSearchService service) : ITimedTool
{
    /// <summary>Slack on top of the search budget: the fan-out ends itself at the budget and still has to format.</summary>
    private static readonly TimeSpan BudgetSlack = TimeSpan.FromSeconds(5);

    /// <summary>The search budget the settings allow (up to 60 s) plus slack, so a long budget is not cut at 30 s.</summary>
    public TimeSpan Timeout => service.Budget + BudgetSlack;

    public const string Name = "web_search";

    public ToolSpec Spec { get; } = new(
        Name,
        """
        联网搜索。返回的是一组线索，不是答案：标题和摘要由搜索引擎自己写，可能过期、断章取义或与查询词只是表面相关。
        结果里不含发布日期就说明索引没有给出，不代表内容新。要据此下结论，必须用 web_fetch 打开原始网页核对。
        排序靠前的条目可能被同一批结果里的多个来源重复命中，这只能说明它们被多处收录，不代表更可信。
        """,
        """
        {"type":"object","properties":{
        "query":{"type":"string","description":"查询词。用关键词而不是整句话；中文可以，但专有名词用原文写法（英文原名）命中率更高。"},
        "kind":{"type":"string","enum":["general","news","academic","code","encyclopedia"],"description":"限定来源类型，默认 general。只索引该类型的专门站点，范围窄，结果太少时换成 general。"},
        "recency":{"type":"string","enum":["day","week","month","year"],"description":"只保留有明确日期且在此范围内的结果；日期不详的结果会被一并丢掉，所以它会让结果变少。"},
        "limit":{"type":"integer","description":"向每个引擎索取多少条，默认取设置值（通常 8），上限 20。"}},
        "required":["query"]}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await InvokeCoreAsync(argumentsJson, ct).ConfigureAwait(false);
        }
        finally
        {
            Partner.Core.Diagnostics.LatencyTracker.Record("search", watch.Elapsed);
        }
    }

    private async Task<ToolResult> InvokeCoreAsync(string argumentsJson, CancellationToken ct)
    {
        string? query;
        string? kind;
        string? recency;
        int? limit;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;

            query = Text(root, "query");
            kind = Text(root, "kind");
            recency = Text(root, "recency");
            limit = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("limit", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number)
                    ? number
                    : null;
        }
        catch (JsonException error)
        {
            return new ToolResult(false, $"invalid_args: 参数不是合法 JSON（{error.Message}）");
        }

        return await service.SearchAsync(query, kind, recency, limit, ct).ConfigureAwait(false);
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
