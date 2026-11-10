using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Memory;

namespace Partner.Api.Tests;

/// <summary>CRUD, validation, secret handling, memory purge, and the settings-preserve rule for services.</summary>
public sealed class ServiceEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "service-endpoints";

    private static object Body(string name, int port, bool enabled = false, bool inherit = true, string? baseUrl = null) => new
    {
        name,
        enabled,
        port,
        protocol = "openai",
        model = "m",
        contextTokens = 8000,
        prompt = "",
        recall = new { enabled = true, limit = 8, tokenLimit = 2000 },
        summary = new { enabled = true, intervalMinutes = 10, maxWaitMinutes = 60 },
        upstream = new
        {
            inherit,
            baseUrl,
            model = baseUrl is null ? null : "up",
            apiKey = baseUrl is null ? null : "k",
        },
    };

    private async Task<JsonElement> CreateAsync(object body)
    {
        var response = await _http.PostAsJsonAsync("/api/services", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Services_crud_round_trip()
    {
        var port = FakeUpstream.FreePort();
        var created = await CreateAsync(Body("alpha", port));
        var id = created.GetProperty("settings").GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(created.GetProperty("token").GetString()));

        var list = await _http.GetFromJsonAsync<JsonElement>("/api/services");
        Assert.Single(list.EnumerateArray());

        var one = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Equal("alpha", one.GetProperty("settings").GetProperty("name").GetString());
        Assert.False(one.GetProperty("state").GetProperty("running").GetBoolean());

        var put = await _http.PutAsJsonAsync($"/api/services/{id}", Body("beta", port));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var renamed = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Equal("beta", renamed.GetProperty("settings").GetProperty("name").GetString());

        var delete = await _http.DeleteAsync($"/api/services/{id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/services")).EnumerateArray());
    }

    [Fact]
    public async Task Duplicate_ports_the_main_api_port_and_a_loopback_upstream_are_rejected()
    {
        var first = await CreateAsync(Body("a", FakeUpstream.FreePort()));

        var duplicate = await _http.PostAsJsonAsync("/api/services", Body("b", first.GetProperty("settings").GetProperty("port").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var mainPort = await _http.PostAsJsonAsync("/api/services", Body("c", _port));
        Assert.Equal(HttpStatusCode.BadRequest, mainPort.StatusCode);

        // A service's upstream pointed back at another service's loopback port would recurse through this host.
        var servicePort = first.GetProperty("settings").GetProperty("port").GetInt32();
        var loop = await _http.PostAsJsonAsync("/api/services",
            Body("d", FakeUpstream.FreePort(), inherit: false, baseUrl: $"http://127.0.0.1:{servicePort}"));
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);
    }

    [Fact]
    public async Task The_plaintext_token_is_returned_once_and_never_echoed_back()
    {
        var created = await CreateAsync(Body("t", FakeUpstream.FreePort()));
        var id = created.GetProperty("settings").GetProperty("id").GetString()!;
        var first = created.GetProperty("token").GetString();

        var listed = await _http.GetFromJsonAsync<JsonElement>("/api/services");
        var view = listed[0].GetProperty("settings");
        Assert.False(view.TryGetProperty("token", out _)); // masked
        Assert.True(view.GetProperty("tokenSet").GetBoolean());

        var rotated = await _http.PostAsync($"/api/services/{id}/token", content: null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.NotEqual(first, second);

        var again = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.False(again.GetProperty("settings").TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Purge_memories_deletes_only_this_services_origin()
    {
        var created = await CreateAsync(Body("p", FakeUpstream.FreePort()));
        var id = created.GetProperty("settings").GetProperty("id").GetString()!;

        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "main-record",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "a main fact",
        });
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "service-record",
            Tier = MemoryTier.Episodic,
            Scope = MemoryScope.Recall,
            Body = "[对外服务:p] a summary",
            Origin = MemoryOrigins.Service(id),
        });

        var deleted = await _http.DeleteAsync($"/api/services/{id}?purgeMemories=true");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(1, (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deletedMemories").GetInt32());

        Assert.NotNull((await _context.Memory.GetAsync("service-record"))!.ValidTo); // soft-deleted
        Assert.Null((await _context.Memory.GetAsync("main-record"))!.ValidTo); // untouched
    }

    [Fact]
    public async Task A_settings_put_without_services_does_not_clear_the_service_config()
    {
        await CreateAsync(Body("keep", FakeUpstream.FreePort()));

        var put = await _http.PutAsJsonAsync("/api/settings", new { input = new { enabled = true } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var list = await _http.GetFromJsonAsync<JsonElement>("/api/services");
        Assert.Single(list.EnumerateArray());
        Assert.Equal("keep", list[0].GetProperty("settings").GetProperty("name").GetString());
    }
}
