using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// The model protocol is a stored setting like the endpoint itself: the alias the settings file may contain
/// is stored canonically, and a word neither client can speak is refused at save time rather than at turn time.
/// </summary>
public sealed class ModelProtocolEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-model-protocol-tests";

    protected override string ReadinessPath => "/api/settings";

    [Fact]
    public async Task The_model_protocol_round_trips_and_rejects_unknown_values()
    {
        var saved = await _http.PutAsJsonAsync("/api/settings", new { model = new { protocol = "claude" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("anthropic", view.GetProperty("model").GetProperty("protocol").GetString());

        var bad = await _http.PutAsJsonAsync("/api/settings", new { model = new { protocol = "gemini" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var failure = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            failure.GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("model.protocol", StringComparison.Ordinal));
    }
}
