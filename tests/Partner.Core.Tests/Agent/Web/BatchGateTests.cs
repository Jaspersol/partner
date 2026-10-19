using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The gate that tells a failure that looks like a success from a success. Every test here is a case where the
/// naive reading — "the engine returned a full batch, so it worked" — is wrong, or a case where the careful
/// reading would throw away real results.
/// </summary>
public sealed class BatchGateTests
{
    private static readonly SearchQuery Query = new("quantum computing breakthrough");

    [Fact]
    public void Apply_DropsABatchThatMentionsNothingTheUserAskedAbout()
    {
        var decoy = Engine("duckduckgo");
        var companion = Engine("bing");

        var outcome = new BatchGate().Apply(
            [
                FakeEngines.Answered(decoy, Deco(decoy)),
                FakeEngines.Answered(companion, Echo(companion)),
            ],
            Query);

        var rejected = outcome.Batches.Single(verdict => verdict.Engine == decoy);
        Assert.False(rejected.Keep);
        Assert.Equal("decoy", rejected.Rejection);
        Assert.Equal(0, rejected.Echo);

        // Kept for the audit tool: what a served batch contained is the only evidence there is about the serving.
        Assert.Equal(3, rejected.Hits.Count);

        Assert.Equal(3, outcome.Hits.Count);
        Assert.All(outcome.Hits, hit => Assert.Equal("bing", hit.Engine));
    }

    [Fact]
    public void Apply_KeepsALowEchoBatchWhenNothingElseFoundAnything()
    {
        // "The English question answered by a foreign-language source" is a legitimate batch with a low echo.
        // Without a healthy companion there is no evidence it was served, so it is kept.
        var lone = Engine("duckduckgo");

        var outcome = new BatchGate().Apply([FakeEngines.Answered(lone, Deco(lone))], Query);

        Assert.True(Assert.Single(outcome.Batches).Keep);
        Assert.Equal(3, outcome.Hits.Count);
    }

    [Fact]
    public void Apply_DoesNotJudgeABatchSmallerThanThree()
    {
        // The ratio means nothing at n=1: one irrelevant result is an ordinary search.
        var small = Engine("duckduckgo");
        var companion = Engine("bing");

        var outcome = new BatchGate().Apply(
            [
                FakeEngines.Answered(small, Deco(small).Take(2).ToArray()),
                FakeEngines.Answered(companion, Echo(companion)),
            ],
            Query);

        Assert.True(outcome.Batches.Single(verdict => verdict.Engine == small).Keep);
    }

    [Fact]
    public void Apply_ExemptsASingleSiteCatalogueFromTheEchoTest()
    {
        // Wikipedia never echoes the query's phrasing, so its echo is defined as 1 rather than measured; running
        // the check against it would drop a whole legitimate encyclopaedia.
        var wiki = FakeEngines.Make("wikipedia", ContentKinds.Encyclopedia, singleSite: true);
        var companion = Engine("bing");

        var outcome = new BatchGate().Apply(
            [
                FakeEngines.Answered(wiki, Deco(wiki)),
                FakeEngines.Answered(companion, Echo(companion)),
            ],
            Query);

        var exempt = outcome.Batches.Single(verdict => verdict.Engine == wiki);
        Assert.True(exempt.Keep);
        Assert.Equal(1, exempt.Echo);
    }

    [Fact]
    public void Apply_RejectsABlockVerdictBeforeLookingAtTheHits()
    {
        var engine = Engine("duckduckgo");

        var outcome = new BatchGate().Apply([FakeEngines.Empty(engine, blocked: true)], Query);

        var verdict = Assert.Single(outcome.Batches);
        Assert.False(verdict.Keep);
        Assert.Equal("blocked", verdict.Rejection);
    }

    [Fact]
    public void Apply_RejectsAnEmptyBatch()
    {
        var engine = Engine("duckduckgo");

        var outcome = new BatchGate().Apply([FakeEngines.Empty(engine)], Query);

        Assert.Equal("empty", Assert.Single(outcome.Batches).Rejection);
        Assert.Empty(outcome.Hits);
    }

    [Fact]
    public void Apply_AnyKeptIsFalseWhenEveryEngineFailed()
    {
        // This is the flag the health store reads as "was there a companion", so it has to mean a batch survived
        // the gate rather than that a batch was returned.
        var blocked = Engine("duckduckgo");
        var empty = Engine("bing");

        var nothing = new BatchGate().Apply(
            [FakeEngines.Empty(blocked, blocked: true), FakeEngines.Empty(empty)],
            Query);

        Assert.False(nothing.AnyKept);

        var something = new BatchGate().Apply(
            [FakeEngines.Empty(blocked, blocked: true), FakeEngines.Answered(empty, Echo(empty))],
            Query);

        Assert.True(something.AnyKept);
    }

    [Fact]
    public void Apply_MergesTheKeptHitsOfEveryBatchThatSurvived()
    {
        var one = Engine("duckduckgo");
        var two = Engine("bing");

        var outcome = new BatchGate().Apply(
            [FakeEngines.Answered(one, Echo(one)), FakeEngines.Answered(two, Echo(two))],
            Query);

        Assert.Equal(6, outcome.Hits.Count);
        Assert.Equal(2, outcome.Batches.Count);
    }

    [Fact]
    public void Apply_JudgesEachBatchOnItsOwnEcho()
    {
        // Two batches, one of which mentions the query once. The check is batch-level and never per-result: a
        // single relevant result is enough to show the batch is about the subject.
        var thin = Engine("duckduckgo");
        var companion = Engine("bing");

        var hits = new[]
        {
            FakeEngines.Hit("duckduckgo", "quantum computing breakthrough", "", 0),
            FakeEngines.Hit("duckduckgo", "Big sale", "everything must go", 1),
            FakeEngines.Hit("duckduckgo", "Weather", "rain tomorrow", 2),
        };

        var outcome = new BatchGate().Apply(
            [
                FakeEngines.Answered(thin, hits),
                FakeEngines.Answered(companion, Echo(companion)),
            ],
            Query);

        var verdict = outcome.Batches.Single(each => each.Engine == thin);
        Assert.True(verdict.Keep);

        // One of three: above the provisional ceiling, which is why it survives.
        Assert.Equal(1.0 / 3, verdict.Echo, 3);
    }

    private static IWebSearchEngine Engine(string id) => FakeEngines.Make(id, ContentKinds.General);

    /// <summary>A healthy batch: every result is about the question.</summary>
    private static SearchHit[] Echo(IWebSearchEngine engine) =>
        [
            FakeEngines.Hit(engine.Traits.Id, $"quantum computing breakthrough — part 1", "computing", 0),
            FakeEngines.Hit(engine.Traits.Id, "A breakthrough in quantum computing", "quantum", 1),
            FakeEngines.Hit(engine.Traits.Id, "Quantum computing, explained", "breakthrough", 2),
        ];

    /// <summary>A batch served to a client the source no longer believes is a person: full, and about nothing.</summary>
    private static SearchHit[] Deco(IWebSearchEngine engine) =>
        [
            FakeEngines.Hit(engine.Traits.Id, "Big sale", "everything must go", 0),
            FakeEngines.Hit(engine.Traits.Id, "Weather", "rain tomorrow", 1),
            FakeEngines.Hit(engine.Traits.Id, "Sign in", "please verify you are human", 2),
        ];
}
