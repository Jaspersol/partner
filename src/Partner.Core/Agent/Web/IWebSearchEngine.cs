namespace Partner.Core.Agent.Web;

/// <summary>
/// A search source, reduced to the two things only it can know: how to phrase the request, and how to read the
/// answer. Transport, throttling, circuit breaking, filtering and labelling belong to the pipeline: they are the
/// same for every engine, and a new engine must not have to re-understand them.
/// </summary>
public interface IWebSearchEngine
{
    EngineTraits Traits { get; }

    /// <summary>Builds the request. Does not send it: the transport owns the client, the headers and the proxy.</summary>
    HttpRequestMessage Build(SearchQuery query);

    /// <summary>
    /// Reads a response. Never judges whether the results are <i>good</i>: that is <see cref="BatchGate"/>'s job,
    /// and keeping it out of here is what lets a parser be tested against an archived response.
    /// </summary>
    /// <param name="response">
    /// The status, the final URI and the already-decoded, already-capped body. Deliberately not the live
    /// <see cref="HttpResponseMessage"/>: an engine that can read a stream again can read past the cap and can
    /// hold a connection the transport wants back.
    /// </param>
    /// <param name="query">The query, so a parser can build a URL for a result that omits one.</param>
    /// <param name="follow">
    /// How an engine that needs a second request gets one (weather is a lookup then a forecast). Every other
    /// engine ignores it, and a test passes a fake.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<ParseResult> ParseAsync(
        WebResponse response,
        SearchQuery query,
        IEngineFollowUp follow,
        CancellationToken ct = default);
}

/// <summary>
/// A second request, for the few engines whose answer is not in the first response. Kept as a narrow interface
/// rather than by handing the engine the transport: an engine gains the ability to make one more request, not
/// the ability to make requests.
/// </summary>
public interface IEngineFollowUp
{
    /// <summary>Sends one GET and returns the decoded body, or null with a reason when it could not be read.</summary>
    Task<(string? Body, string? Error)> GetAsync(Uri url, CancellationToken ct = default);
}

/// <summary>The engines this build knows about, in the order the pool planner considers them.</summary>
public static class WebEngines
{
    /// <summary>
    /// The one place the engine set is listed, so the settings page, the planner and the health store cannot
    /// disagree about what exists.
    /// <para>
    /// Brave is deliberately absent: its result page is rendered client-side, so there is no markup to write a
    /// parser against and no way to archive one for replay fixtures. An engine whose parser cannot be tested
    /// would report <c>unparseable</c> in production and blame the network. Only engines whose shapes are
    /// long-standing and documented are included.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IWebSearchEngine> All { get; } =
    [
        new Engines.OpenMeteoEngine(),
        new Engines.WikipediaEngine(),
        new Engines.ArxivEngine(),
        new Engines.GitHubEngine(),
        new Engines.HackerNewsEngine(),
        new Engines.DuckDuckGoEngine(),
        new Engines.BingEngine(),
    ];

    public static IWebSearchEngine? Find(string id) =>
        All.FirstOrDefault(engine => string.Equals(engine.Traits.Id, id, StringComparison.Ordinal));

    public static IReadOnlyList<string> Ids { get; } = All.Select(engine => engine.Traits.Id).ToList();
}
