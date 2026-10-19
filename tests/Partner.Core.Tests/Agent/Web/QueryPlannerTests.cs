using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The first quality gate: asking the right people. There is no routing table anywhere in the pipeline — every
/// decision here reads what the engine declared about itself — so these tests are also the specification of what
/// an engine has to declare to be reachable at all.
/// </summary>
public sealed class QueryPlannerTests
{
    private static EnginePool Plan(SearchQuery query, params IWebSearchEngine[] engines) =>
        new QueryPlanner(engines).Plan(query, WebPlanContext.Open);

    [Fact]
    public void Plan_PutsTheKindSpecialistAheadOfBreadth()
    {
        var arxiv = FakeEngines.Make("arxiv", ContentKinds.Academic, role: EnginePoolRole.Specialist);
        var duck = FakeEngines.Make("duckduckgo", ContentKinds.General, role: EnginePoolRole.Base);

        var pool = Plan(new SearchQuery("transformer scaling laws", SearchKinds.Academic), duck, arxiv);

        Assert.Equal(new[] { "arxiv", "duckduckgo" }, pool.Engines.Select(planned => planned.Engine.Traits.Id));
        Assert.Equal("kind=academic", pool.Engines[0].Why);
    }

    [Fact]
    public void Plan_AsksAnEngineThatHandlesTheQuestionEvenWhenItsScriptIsWrong()
    {
        // Shape routing comes first and is exempt from the script filter: an engine that matched the question
        // offline declared, by doing so, that it can answer this query. Weather is a place plus a word, and both
        // may be Chinese.
        var weather = FakeEngines.Make(
            "open-meteo",
            ContentKinds.Weather,
            QueryScripts.Latin,
            role: EnginePoolRole.Specialist,
            handles: text => text.Contains("天气", StringComparison.Ordinal));

        var pool = Plan(new SearchQuery("北京天气"), weather);

        var planned = Assert.Single(pool.Engines);
        Assert.Equal("问题形态直答", planned.Why);
    }

    [Fact]
    public void Plan_SaysWhyALatinOnlySpecialistWasNotAskedForAChineseQuery()
    {
        var news = FakeEngines.Make(
            "news",
            ContentKinds.News,
            QueryScripts.Latin,
            role: EnginePoolRole.Specialist,
            handles: null);

        var pool = Plan(new SearchQuery("今天的新闻", SearchKinds.News), news);

        Assert.Empty(pool.Engines);
        Assert.Contains(pool.Skipped, reason => reason.Contains("只检索拉丁文查询", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RoutesRecencyToANewsSpecialist()
    {
        var news = FakeEngines.Make("news", ContentKinds.News, role: EnginePoolRole.Specialist);

        var pool = Plan(new SearchQuery("rust release", SearchKinds.General, SearchRecency.Week), news);

        Assert.Equal("时效诉求", Assert.Single(pool.Engines).Why);
    }

    [Fact]
    public void Plan_NeverRoutesToAnEngineThatNeedsAnAccount()
    {
        // A capability that quietly depends on somebody's login is not a capability that can be promised, so the
        // planner refuses these whatever else they declare.
        var selfHosted = FakeEngines.Make("searxng", ContentKinds.General, needsCredentials: true);
        var duck = FakeEngines.Make("duckduckgo", ContentKinds.General);

        var pool = Plan(new SearchQuery("anything"), selfHosted, duck);

        Assert.Equal("duckduckgo", Assert.Single(pool.Engines).Engine.Traits.Id);

        // Not even in the skipped list: it is not a source the user could have switched on, so naming it in the
        // answer would be telling them about a choice they do not have.
        Assert.Empty(pool.Skipped);
    }

    [Fact]
    public void Plan_ExcludesASwitchedOffEngineWithTheUsersOwnReason()
    {
        var duck = FakeEngines.Make("duckduckgo", ContentKinds.General);
        var context = new WebPlanContext(new HashSet<string>(StringComparer.Ordinal) { "duckduckgo" }, _ => null);

        var pool = new QueryPlanner([duck]).Plan(new SearchQuery("anything"), context);

        Assert.Empty(pool.Engines);
        Assert.Contains(pool.Skipped, reason => reason.Contains("你已在设置里停用", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ExcludesACoolingEngineWithTheBreakersWording()
    {
        var duck = FakeEngines.Make("duckduckgo", ContentKinds.General);
        var context = new WebPlanContext(
            new HashSet<string>(StringComparer.Ordinal),
            id => id == "duckduckgo" ? "熔断中，还有约 5 分钟（被拦截）" : null);

        var pool = new QueryPlanner([duck]).Plan(new SearchQuery("anything"), context);

        Assert.Empty(pool.Engines);
        Assert.Contains(pool.Skipped, reason => reason.Contains("熔断中", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_AddsReservesOnlyWhenBreadthIsThin()
    {
        var baseOne = FakeEngines.Make("a", ContentKinds.General, role: EnginePoolRole.Base);
        var baseTwo = FakeEngines.Make("b", ContentKinds.General, role: EnginePoolRole.Base);
        var reserve = FakeEngines.Make("r", ContentKinds.General, role: EnginePoolRole.Reserve);

        var enough = Plan(new SearchQuery("x"), baseOne, baseTwo, reserve);
        Assert.Equal(new[] { "a", "b" }, enough.Engines.Select(planned => planned.Engine.Traits.Id));
        Assert.Contains(enough.Skipped, reason => reason.Contains("基础源够用", StringComparison.Ordinal));

        // A reserve is a bounded substitute for breadth that is missing, not a queue.
        var thin = Plan(new SearchQuery("x"), baseOne, reserve);
        Assert.Equal(new[] { "a", "r" }, thin.Engines.Select(planned => planned.Engine.Traits.Id));
    }

    [Fact]
    public void Plan_RotatesWhoSurvivesThePoolCap()
    {
        var engines = Enumerable.Range(0, 8)
            .Select(index => FakeEngines.Make($"e{index}", ContentKinds.General))
            .ToArray();

        var first = new QueryPlanner(engines).Plan(new SearchQuery("x"), WebPlanContext.Open);
        var second = new QueryPlanner(engines).Plan(new SearchQuery("x"), WebPlanContext.Open with { Rotation = 1 });

        Assert.Equal(QueryPlanner.MaxPool, first.Engines.Count);
        Assert.Contains(first.Skipped, reason => reason.Contains("名额上限", StringComparison.Ordinal));

        // Without the shift the tail of a long group would never be asked, and nobody would ever find out why.
        Assert.NotEqual(
            first.Engines.Select(planned => planned.Engine.Traits.Id),
            second.Engines.Select(planned => planned.Engine.Traits.Id));
    }

    [Fact]
    public void Plan_IgnoresAnEngineThatIndexesNothingWithoutARoutingRule()
    {
        // Nothing to say about it either way: it declared no kind and no shape, so it is not a source that was
        // passed over, it is a source that was never a candidate.
        var opaque = FakeEngines.Make("opaque", ContentKinds.None);

        var pool = Plan(new SearchQuery("x"), opaque);

        Assert.Empty(pool.Engines);
        Assert.Empty(pool.Skipped);
    }

    [Fact]
    public void MaskOf_IgnoresAnUnknownKindRatherThanRefusing()
    {
        Assert.Equal(ContentKinds.General, QueryPlanner.MaskOf("nonsense"));
        Assert.Equal(ContentKinds.General, QueryPlanner.MaskOf(null));
        Assert.Equal(ContentKinds.Code, QueryPlanner.MaskOf("code"));
    }

    [Fact]
    public void TheShippedEnginesDeclareThemselvesConsistently()
    {
        // The engine set is listed in exactly one place. These are the invariants the rest of the pipeline reads
        // that list on, so a new engine that breaks one of them breaks a gate several files away.
        Assert.NotEmpty(WebEngines.All);
        Assert.Equal(WebEngines.All.Count, WebEngines.Ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(WebEngines.All, engine => Assert.False(engine.Traits.NeedsCredentials));
        Assert.All(WebEngines.All, engine => Assert.NotEqual(ContentKinds.None, engine.Traits.Kinds));
        Assert.All(WebEngines.All, engine => Assert.NotEqual(default, engine.Traits.Scripts));

        // A single-site catalogue is exempt from the hostname filter by construction: its own results live on its
        // own domain, and filtering them would leave the engine with nothing.
        Assert.All(
            WebEngines.All.Where(engine => engine.Traits.SingleSite),
            engine => Assert.True(engine.Traits.ExemptFromHostFilter));

        // A record answers the question itself rather than pointing at a page that might, which is why the merger
        // weighs it above the whole default pool — so it must not be filed as a general index.
        Assert.All(
            WebEngines.All.Where(engine => engine.Traits.Nature == ResultNature.Record),
            engine => Assert.NotEqual(ContentKinds.General, engine.Traits.Kinds));
    }

    [Fact]
    public void TheShippedSetSaysWhyTheAcademicIndexWasNotAskedAChineseQuestion()
    {
        // End to end over the real engine list rather than a fake: a Chinese academic query is exactly the case
        // the script declaration on arXiv exists to answer, and the answer has to reach the user's screen.
        var pool = new QueryPlanner(WebEngines.All).Plan(
            new SearchQuery("图神经网络综述", SearchKinds.Academic),
            WebPlanContext.Open);

        Assert.NotEmpty(pool.Engines);
        Assert.DoesNotContain(pool.Engines, planned => planned.Engine.Traits.Id == "arxiv");

        var reason = Assert.Single(pool.Skipped, each => each.Contains("arXiv", StringComparison.Ordinal));
        Assert.Contains("只检索拉丁文查询", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedSetNeverAsksALatinOnlyIndexAChineseQuestion()
    {
        var pool = new QueryPlanner(WebEngines.All).Plan(
            new SearchQuery("量子计算的最新进展"),
            WebPlanContext.Open);

        Assert.NotEmpty(pool.Engines);
        Assert.All(pool.Engines, planned => Assert.NotEqual(QueryScripts.Latin, planned.Engine.Traits.Scripts));
    }
}
