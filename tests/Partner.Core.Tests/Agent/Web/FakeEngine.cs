using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// An engine that is nothing but its declaration.
/// <para>
/// The planner, the gate, the breaker and the merger never look at a response — they read <see cref="Traits"/>
/// and hits — so the tests of those parts do not need a network, a parser or a content type, and a failure in
/// them can only be the logic under test. <see cref="Build"/> and <see cref="ParseAsync"/> exist to satisfy the
/// interface and are never reached.
/// </para>
/// </summary>
internal sealed class FakeEngine(EngineTraits traits) : IWebSearchEngine
{
    public EngineTraits Traits { get; } = traits;

    public HttpRequestMessage Build(SearchQuery query) => new(HttpMethod.Get, "https://example.test/");

    public Task<ParseResult> ParseAsync(
        WebResponse response,
        SearchQuery query,
        IEngineFollowUp follow,
        CancellationToken ct = default) => Task.FromResult(ParseResult.Empty());
}

internal static class FakeEngines
{
    /// <summary>An engine with everything a pool cares about exposed as an argument, and sane defaults for the rest.</summary>
    public static FakeEngine Make(
        string id,
        ContentKinds kinds = ContentKinds.General,
        QueryScripts scripts = QueryScripts.Any,
        EnginePoolRole role = EnginePoolRole.Base,
        bool singleSite = false,
        ResultNature nature = ResultNature.Ranked,
        Func<string, bool>? handles = null,
        RateLimit? rate = null,
        bool needsCredentials = false) =>
        new(new EngineTraits(
            id,
            id.ToUpperInvariant(),
            kinds,
            scripts,
            handles,
            nature,
            singleSite,
            ExemptFromHostFilter: singleSite,
            rate ?? RateLimit.Unlimited,
            role,
            needsCredentials));

    /// <summary>A result. Only title, snippet, engine and rank are ever read by the gates; the rest is filler.</summary>
    public static SearchHit Hit(
        string engine,
        string title,
        string snippet = "",
        int rank = 0,
        string? url = null,
        DateTimeOffset? published = null,
        DateSource dateSource = DateSource.None) =>
        new(title, url ?? $"https://{engine}.test/{rank}", snippet, published, dateSource, engine, rank);

    /// <summary>An attempt that brought back results.</summary>
    public static EngineAttempt Answered(IWebSearchEngine engine, params SearchHit[] hits) =>
        new(engine, "广度", ParseResult.Ok(hits), null, TimeSpan.FromMilliseconds(50));

    /// <summary>An attempt that did not, with the failure prefix the pipeline would have produced.</summary>
    public static EngineAttempt Failed(IWebSearchEngine engine, string failure) =>
        new(engine, "广度", null, failure, TimeSpan.FromMilliseconds(50));

    /// <summary>An attempt that parsed cleanly and found nothing.</summary>
    public static EngineAttempt Empty(IWebSearchEngine engine, bool blocked = false) =>
        new(engine, "广度", blocked ? ParseResult.Blocked("验证码") : ParseResult.Empty(), null, TimeSpan.FromMilliseconds(50));

    /// <summary>An attempt whose page no longer looks like what the parser was written for.</summary>
    public static EngineAttempt Unparseable(IWebSearchEngine engine, string note = "结果块不见了") =>
        new(engine, "广度", ParseResult.Unparseable(note), null, TimeSpan.FromMilliseconds(50));
}
