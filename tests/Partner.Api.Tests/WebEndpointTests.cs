using Partner.Core.Agent.Web;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// The settings page's view of the web capability. The page renders one row per engine out of this payload, and
/// the desktop suite stubs it, so the only place the real shape is pinned is here.
/// </summary>
public sealed class WebEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-web-tests";

    protected override string ReadinessPath => "/api/web";

    [Fact]
    public async Task A_fresh_install_reports_the_section_as_on()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/web");

        // The one section in this file that ships enabled. The plan called for fail-closed; the owner asked for
        // this instead, and the page states why next to the switch.
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(12, view.GetProperty("timeBudgetSeconds").GetInt32());
        Assert.Equal(8, view.GetProperty("maxResults").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("proxy").ValueKind);
        Assert.Equal(Path.Combine(_workspace, "web", "health.json"), view.GetProperty("healthPath").GetString());
    }

    [Fact]
    public async Task Every_shipped_engine_is_described_and_none_is_failing_yet()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/web");
        var engines = view.GetProperty("engines").EnumerateArray().ToList();

        // The registry is the only source for this list, so a new engine reaches the page by being registered.
        Assert.Equal(WebEngines.Ids, engines.Select(engine => engine.GetProperty("id").GetString()));

        Assert.All(engines, engine => Assert.False(engine.GetProperty("disabled").GetBoolean()));
        Assert.All(engines, engine => Assert.False(engine.GetProperty("cooling").GetBoolean()));
        Assert.All(engines, engine => Assert.Equal(0, engine.GetProperty("strikes").GetInt32()));
        Assert.All(engines, engine => Assert.Equal(JsonValueKind.Null, engine.GetProperty("lastReason").ValueKind));
    }

    [Fact]
    public async Task The_two_shapes_of_engine_are_reported_as_the_page_draws_them()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/web");
        var engines = view.GetProperty("engines").EnumerateArray().ToList();

        var weather = Engine(engines, "open-meteo");
        Assert.Equal("record", weather.GetProperty("nature").GetString());
        Assert.Equal(new[] { "weather" }, Kinds(weather));
        Assert.True(weather.GetProperty("singleSite").GetBoolean());

        var duck = Engine(engines, "duckduckgo");
        Assert.Equal("ranked", duck.GetProperty("nature").GetString());
        Assert.Equal(new[] { "general", "news" }, Kinds(duck));
        Assert.False(duck.GetProperty("singleSite").GetBoolean());
    }

    [Fact]
    public async Task The_switch_the_bounds_and_the_disabled_list_all_round_trip()
    {
        var saved = await _http.PutAsJsonAsync("/api/settings", new
        {
            web = new
            {
                enabled = false,
                timeBudgetSeconds = 20,
                maxResults = 5,
                proxy = "http://127.0.0.1:7890",
                disabledEngines = new[] { "duckduckgo" },
            },
        });
        saved.EnsureSuccessStatusCode();

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/web");
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(20, view.GetProperty("timeBudgetSeconds").GetInt32());
        Assert.Equal(5, view.GetProperty("maxResults").GetInt32());
        Assert.Equal("http://127.0.0.1:7890", view.GetProperty("proxy").GetString());

        var engines = view.GetProperty("engines").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "duckduckgo" },
            engines.Where(engine => engine.GetProperty("disabled").GetBoolean())
                .Select(engine => engine.GetProperty("id").GetString()));

        // Switching an engine off is the only thing this payload lets a person change about the rows: the cooldown
        // is the breaker's, and no attempt to edit it here is possible.
        Assert.All(engines, engine => Assert.False(engine.GetProperty("cooling").GetBoolean()));
    }

    [Fact]
    public async Task Values_the_number_inputs_could_not_enforce_are_refused_by_the_api()
    {
        var tooLong = await _http.PutAsJsonAsync("/api/settings", new { web = new { timeBudgetSeconds = 600 } });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var errors = (await tooLong.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors").EnumerateArray().Select(error => error.GetString()!).ToList();
        Assert.Contains(errors, error => error.Contains("web.timeBudgetSeconds", StringComparison.Ordinal));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _http.PutAsJsonAsync("/api/settings", new { web = new { maxResults = 99 } })).StatusCode);

        // A proxy is only a proxy if it is an absolute http(s) address; the field is Latin free text otherwise.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _http.PutAsJsonAsync("/api/settings", new { web = new { proxy = "socks5://127.0.0.1:1080" } })).StatusCode);

        // None of them wrote anything.
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/web");
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(12, view.GetProperty("timeBudgetSeconds").GetInt32());
        Assert.Equal(8, view.GetProperty("maxResults").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("proxy").ValueKind);
    }

    private static JsonElement Engine(List<JsonElement> engines, string id) =>
        engines.Single(engine => engine.GetProperty("id").GetString() == id);

    private static IEnumerable<string?> Kinds(JsonElement engine) =>
        engine.GetProperty("kinds").EnumerateArray().Select(kind => kind.GetString());
}
