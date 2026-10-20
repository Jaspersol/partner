using Partner.Core.Agent.Web;

namespace Partner.Api.Endpoints;

/// <summary>One engine as the settings page needs it.</summary>
/// <param name="Id">The id the settings file stores in its disabled list.</param>
/// <param name="Nature">Ranked documents, or a record the publisher owns.</param>
/// <param name="Kinds">Content types it indexes, by name.</param>
/// <param name="SingleSite">A catalogue of one site.</param>
/// <param name="Disabled">Switched off by the user.</param>
/// <param name="Cooling">Currently in the breaker's cooldown.</param>
/// <param name="Strikes">Consecutive failures.</param>
public sealed record WebEngineView(
    string Id,
    string Name,
    string Nature,
    IReadOnlyList<string> Kinds,
    bool SingleSite,
    bool Disabled,
    bool Cooling,
    DateTimeOffset? Until,
    int Strikes,
    string? LastReason);

/// <summary>
/// The web section as the settings page reads it. Health state is included because the settings file is not where
/// a person looks: an engine that stopped answering should say so where the switch that would disable it lives.
/// </summary>
public sealed record WebView(
    bool Enabled,
    string? Proxy,
    int TimeBudgetSeconds,
    int MaxResults,
    IReadOnlyList<WebEngineView> Engines,
    string HealthPath);

public static class WebEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/web", (ApiContext ctx) =>
        {
            var settings = ctx.Settings.Load().Web;
            var health = ctx.Web.Health.Snapshot(settings.DisabledEngines).ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);

            var engines = WebEngines.All.Select(engine =>
            {
                var traits = engine.Traits;
                health.TryGetValue(traits.Id, out var state);

                return new WebEngineView(
                    traits.Id,
                    traits.Name,
                    traits.Nature == ResultNature.Record ? "record" : "ranked",
                    Names(traits.Kinds),
                    traits.SingleSite,
                    settings.DisabledEngines.Contains(traits.Id, StringComparer.Ordinal),
                    state?.Cooling ?? false,
                    state?.Until,
                    state?.Strikes ?? 0,
                    state?.LastReason);
            }).ToList();

            return Results.Ok(new WebView(
                settings.Enabled,
                settings.Proxy,
                settings.TimeBudgetSeconds,
                settings.MaxResults,
                engines,
                ctx.Web.Health.FilePath));
        });
    }

    /// <summary>The flags of a <see cref="ContentKinds"/> value, by name. <c>none</c> for an engine with no rule of its own.</summary>
    private static IReadOnlyList<string> Names(ContentKinds kinds)
    {
        var names = new List<string>();
        foreach (var kind in Enum.GetValues<ContentKinds>())
        {
            if (kind != ContentKinds.None && kinds.HasFlag(kind))
            {
                names.Add(kind.ToString().ToLowerInvariant());
            }
        }

        return names.Count == 0 ? ["none"] : names;
    }
}
