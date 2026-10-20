using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>The HTTP face of the engine module: what the settings page reads and the three things it can ask for.</summary>
public sealed class EnginePackageEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-engine-package-tests";

    [Fact]
    public async Task Status_reports_the_root_and_each_engine_with_what_is_missing()
    {
        var json = await _http.GetFromJsonAsync<JsonElement>("/api/local-engines/package");

        Assert.False(json.GetProperty("customRoot").GetBoolean());
        Assert.EndsWith("engines", json.GetProperty("root").GetString());
        Assert.True(json.GetProperty("tts").TryGetProperty("missing", out _));
        Assert.Equal(45103, json.GetProperty("kwsDefaults").GetProperty("port").GetInt32());
        Assert.Equal("idle", json.GetProperty("download").GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Download_without_a_published_address_says_why_instead_of_pretending()
    {
        using var response = await _http.PostAsync("/api/local-engines/package/download", null);

        // If the machine running the suite has the override variables set the engines may legitimately be fetchable.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_folder_with_no_engines_is_refused_with_a_reason()
    {
        using var response = await _http.PutAsJsonAsync("/api/local-engines/package/root", new { root = _workspace });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("没有找到完整的引擎", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_endpoints_need_the_token()
    {
        using var anonymous = new HttpClient { BaseAddress = _http.BaseAddress };

        using var response = await anonymous.GetAsync("/api/local-engines/package");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
