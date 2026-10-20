using Partner.Core.Sessions;

namespace Partner.Core.Tests.Sessions;

public sealed class SummaryFactsTests
{
    [Fact]
    public void The_facts_line_is_split_into_statements()
    {
        var summary = "Goal: 订机票\nDecided: 周五出发\nFacts: 用户叫王小明；护照号尾号 4821；偏好靠窗座位。\nOpen: 回程日期";

        Assert.Equal(
            new[] { "用户叫王小明", "护照号尾号 4821", "偏好靠窗座位" },
            SummaryFacts.From(summary));
    }

    [Fact]
    public void A_chinese_label_and_colon_are_understood()
    {
        Assert.Equal(new[] { "项目代号是北斗" }, SummaryFacts.From("目标：查进度\n事实：项目代号是北斗"));
    }

    [Fact]
    public void No_facts_line_means_nothing_is_remembered()
    {
        Assert.Empty(SummaryFacts.From("前面的对话：用户反复问候。"));
        Assert.Empty(SummaryFacts.From(null));
        Assert.Empty(SummaryFacts.From("Facts:"));
    }

    [Fact]
    public void Statements_are_bounded_in_length_and_number()
    {
        var many = string.Join("；", Enumerable.Range(0, 30).Select(i => $"第 {i} 条不一样的事实内容"));
        var facts = SummaryFacts.From("Facts: " + many + "；" + new string('长', 500));

        Assert.Equal(8, facts.Count);
        Assert.All(facts, fact => Assert.InRange(fact.Length, 4, 200));
    }

    [Fact]
    public void Too_short_to_mean_anything_is_dropped_and_repeats_collapse()
    {
        Assert.Equal(new[] { "喜欢喝茶" }, SummaryFacts.From("Facts: 好；喜欢喝茶；喜欢喝茶"));
    }
}
