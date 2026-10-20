using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>What one search may cost the context window: a cap on each snippet and on the whole list.</summary>
public sealed class ResultFormatterTests
{
    private static SearchHit Hit(int rank, string snippet) => new(
        $"标题 {rank}",
        $"https://example.org/{rank}",
        snippet,
        Published: null,
        DateSource.None,
        "fake",
        rank);

    private static SearchOutcome Outcome(params SearchHit[] hits) => new(
        new SearchQuery("测试"),
        hits,
        [],
        [],
        [],
        TimeSpan.FromSeconds(1),
        DateTimeOffset.UnixEpoch,
        BudgetExpired: false);

    [Fact]
    public void A_long_abstract_is_cut_to_the_snippet_cap()
    {
        var text = new ResultFormatter().Format(Outcome(Hit(1, new string('文', 5000))), maxResults: 8);

        Assert.DoesNotContain(new string('文', ResultFormatter.MaxSnippetChars + 1), text, StringComparison.Ordinal);
        Assert.Contains(new string('文', ResultFormatter.MaxSnippetChars) + "…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_snippet_is_left_alone()
    {
        var text = new ResultFormatter().Format(Outcome(Hit(1, "一句话摘要")), maxResults: 8);

        Assert.Contains("一句话摘要", text, StringComparison.Ordinal);
        Assert.DoesNotContain("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hit_list_stops_at_the_character_budget_and_says_how_many_were_left_out()
    {
        var hits = Enumerable.Range(1, 20).Select(i => Hit(i, new string('字', ResultFormatter.MaxSnippetChars))).ToArray();

        var text = new ResultFormatter().Format(Outcome(hits), maxResults: 20);

        Assert.Contains("因篇幅省略", text, StringComparison.Ordinal);
        Assert.Contains("1. 标题 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("20. 标题 20", text, StringComparison.Ordinal);
        Assert.Contains(ResultFormatter.LeadWarning, text, StringComparison.Ordinal);
    }
}
