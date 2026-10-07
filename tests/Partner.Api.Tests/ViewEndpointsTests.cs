using System.Net.Http.Headers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Partner.Api;
using Partner.Api.Auth;
using Partner.Api.Voice;
using Partner.Api.Endpoints;
using Partner.Api.Settings;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// The UI data layer, exercised over a real HTTP socket. Each test owns its workspace and its host, so the
/// suite is isolated and order-independent.
/// </summary>
public sealed partial class ViewEndpointsTests : ApiFixture
{
    protected override string WorkspaceName => "partner-api-tests";

    protected override string ReadinessPath => "/api/settings";

    [Fact]
    public async Task Editing_a_session_event_keeps_the_original_and_appends_one_event()
    {
        var seq = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "original",
        });

        var response = await _http.PostAsJsonAsync($"/api/session/{seq}/edit", new { body = "edited", actor = "human" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var item = view.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("edited", item.GetProperty("body").GetString());
        Assert.Equal("original", item.GetProperty("originalBody").GetString());
        Assert.Equal(1, item.GetProperty("revisionCount").GetInt32());
    }

    [Fact]
    public async Task Editing_with_an_empty_body_is_rejected()
    {
        var seq = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "original",
        });

        var response = await _http.PostAsJsonAsync($"/api/session/{seq}/edit", new { body = "   ", actor = "human" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing was appended: a rejected edit must not pollute the log.
        var count = 0;
        await foreach (var _ in _context.Session.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Editing_an_unknown_memory_record_is_a_404()
    {
        var response = await _http.PostAsJsonAsync("/api/memory/nope/edit", new { body = "x", actor = "human" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_memory_record_makes_it_unsearchable()
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Retain,
            Body = "the user prefers dark mode",
        });

        var before = await _http.PostAsJsonAsync("/api/memory/recall", new { query = "dark mode" });
        var beforeHits = await before.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(beforeHits.GetArrayLength() >= 1);

        var delete = await _http.DeleteAsync("/api/memory/m1");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var after = await _http.PostAsJsonAsync("/api/memory/recall", new { query = "dark mode" });
        var afterHits = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, afterHits.GetArrayLength());
    }

    [Fact]
    public async Task Retention_can_be_set_and_released_on_an_existing_record()
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "the deploy target is the frankfurt cluster",
        });

        var retain = await _http.PostAsJsonAsync("/api/memory/m1/scope", new { scope = "Retain" });
        Assert.Equal(HttpStatusCode.OK, retain.StatusCode);
        Assert.Equal(MemoryScope.Retain, (await _context.Memory.GetAsync("m1"))!.Scope);

        var release = await _http.PostAsJsonAsync("/api/memory/m1/scope", new { scope = "Recall" });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(MemoryScope.Recall, (await _context.Memory.GetAsync("m1"))!.Scope);
    }

    [Fact]
    public async Task Retention_leaves_the_tier_alone()
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m1",
            Tier = MemoryTier.Procedural,
            Scope = MemoryScope.Recall,
            Body = "deploy by running the migration before the restart",
        });

        await _http.PostAsJsonAsync("/api/memory/m1/scope", new { scope = "Retain" });

        // Tier is trust, retention is time. Pinning retention to one tier would have left Procedural — a way of
        // doing something rather than a fact — unprotectable.
        Assert.Equal(MemoryTier.Procedural, (await _context.Memory.GetAsync("m1"))!.Tier);
    }

    [Theory]
    [InlineData("Global", HttpStatusCode.BadRequest)]
    [InlineData("nonsense", HttpStatusCode.BadRequest)]
    public async Task An_unknown_retention_value_is_rejected_with_the_allowed_names(string scope, HttpStatusCode expected)
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "the deploy target is the frankfurt cluster",
        });

        var response = await _http.PostAsJsonAsync("/api/memory/m1/scope", new { scope });

        Assert.Equal(expected, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Recall", error.GetProperty("error").GetString()!);
        Assert.Contains("Retain", error.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task Setting_retention_on_an_unknown_record_is_not_found()
    {
        var response = await _http.PostAsJsonAsync("/api/memory/ghost/scope", new { scope = "Retain" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_hand_written_memory_lands_in_the_list_as_an_established_fact()
    {
        var response = await _http.PostAsJsonAsync("/api/memory", new { body = "记得每周五之前提交周报" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Semantic, not Working: a Working record carries a TTL and would have made the note someone just typed
        // disappear on its own.
        Assert.Equal("Semantic", created.GetProperty("tier").GetString());
        Assert.Equal("Recall", created.GetProperty("scope").GetString());

        var all = await _http.GetFromJsonAsync<JsonElement>("/api/memory/all");
        var row = all.EnumerateArray().Single(r => r.GetProperty("recordId").GetString() == created.GetProperty("recordId").GetString());
        Assert.Equal("记得每周五之前提交周报", row.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_hand_written_memory_can_name_its_tier_and_retention()
    {
        var response = await _http.PostAsJsonAsync(
            "/api/memory",
            new { body = "发布前先跑一次迁移脚本", tier = "Procedural", scope = "Retain" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var loaded = await _context.Memory.GetAsync(created.GetProperty("recordId").GetString()!);

        Assert.Equal(MemoryTier.Procedural, loaded!.Tier);
        Assert.Equal(MemoryScope.Retain, loaded.Scope);
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("  ", HttpStatusCode.BadRequest)]
    [InlineData("短", HttpStatusCode.BadRequest)]
    public async Task A_hand_written_memory_has_to_carry_a_statement(string body, HttpStatusCode expected)
    {
        // The distiller refuses turns that carry nothing worth keeping; a route that accepted anything would be a
        // way around that filter rather than a second way through it.
        var response = await _http.PostAsJsonAsync("/api/memory", new { body });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Rebuilding_the_index_does_not_change_search_results()
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Retain,
            Body = "the lamp is in the study",
        });

        var before = await _http.PostAsJsonAsync("/api/memory/recall", new { query = "lamp study" });
        var beforeHits = await before.Content.ReadFromJsonAsync<JsonElement>();
        var beforeIds = beforeHits.EnumerateArray().Select(h => h.GetProperty("recordId").GetString()).ToList();

        var rebuild = await _http.PostAsync("/api/projections/rebuild", content: null);
        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);

        var after = await _http.PostAsJsonAsync("/api/memory/recall", new { query = "lamp study" });
        var afterHits = await after.Content.ReadFromJsonAsync<JsonElement>();
        var afterIds = afterHits.EnumerateArray().Select(h => h.GetProperty("recordId").GetString()).ToList();

        // The scores carry a recency term that decays with the clock, so compare what was found and in what
        // order — that is what "the index is a rebuildable projection" actually claims.
        Assert.Equal(beforeIds, afterIds);
        Assert.NotEmpty(beforeIds);
    }

    [Fact]
    public async Task Canceling_an_unknown_queue_item_is_a_404()
    {
        var response = await _http.PostAsync("/api/queue/ghost/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Queue_view_reports_the_latest_state_per_item()
    {
        await _context.Queue.EnqueueAsync(new InputItem
        {
            ItemId = "i1",
            Source = InputSource.Text,
            Payload = "hello",
            IdempotencyKey = "text:1",
        });

        var response = await _http.PostAsync("/api/queue/i1/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The queue is what is still in play, so a cancelled item is simply not there any more. This
        // used to assert "one entry per item, showing the transitioned state"; it now asserts the absence,
        // which is the same fact stated more strongly — there is no state left in which it could still run.
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        var items = view.GetProperty("items").EnumerateArray().ToList();

        Assert.Empty(items);
        Assert.Equal(0, view.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Settings_reject_a_negative_threshold_and_keep_the_previous_value()
    {
        var bad = await _http.PutAsJsonAsync("/api/settings", new { input = new { silenceThreshold = -1.0 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var current = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(
            RuntimeSettings.Default.Input.SilenceThreshold,
            current.GetProperty("input").GetProperty("silenceThreshold").GetDouble());
    }

    [Fact]
    public async Task Settings_survive_a_new_store_instance()
    {
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            input = new { enabled = true, windowSeconds = 12.5 },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var reopened = new SettingsStore(_context.Settings.Path).Load();

        Assert.True(reopened.Input.Enabled);
        Assert.Equal(12.5, reopened.Input.WindowSeconds);
    }

    [Fact]
    public async Task A_corrupt_settings_file_falls_back_to_defaults()
    {
        var path = _context.Settings.Path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not json");

        var loaded = new SettingsStore(path).Load();

        Assert.Equal(RuntimeSettings.Default, loaded);
    }

    // ---- authentication (fail-closed) ------------------------------------------------------------

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/session")]
    [InlineData("/api/queue")]
    [InlineData("/api/memory")]
    [InlineData("/api/settings")]
    public async Task Every_endpoint_rejects_an_unauthenticated_request(string path)
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        using var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_rejects_an_unauthenticated_write()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        using var edit = await anonymous.PostAsJsonAsync("/api/session/1/edit", new { body = "x", actor = "human" });
        using var cancel = await anonymous.PostAsync("/api/queue/ghost/cancel", content: null);
        using var delete = await anonymous.DeleteAsync("/api/memory/ghost");
        using var scope = await anonymous.PostAsJsonAsync("/api/memory/ghost/scope", new { scope = "Retain" });
        using var create = await anonymous.PostAsJsonAsync("/api/memory", new { body = "写入一条记忆" });
        using var rebuild = await anonymous.PostAsync("/api/projections/rebuild", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, scope.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rebuild.StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_is_rejected()
    {
        using var wrong = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        using var response = await wrong.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_with_the_right_prefix_but_wrong_length_is_rejected()
    {
        using var wrong = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token[..^1]);

        using var response = await wrong.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_configured_token_is_accepted()
    {
        using var response = await _http.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Generated_tokens_are_long_unique_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => TokenAuth.Generate()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t => Assert.Equal(43, t.Length));
        Assert.All(tokens, t => Assert.DoesNotContain('+', t));
        Assert.All(tokens, t => Assert.DoesNotContain('/', t));
        Assert.All(tokens, t => Assert.DoesNotContain('=', t));
    }

    [Fact]
    public void An_absent_environment_token_generates_one_instead_of_allowing_anonymous_access()
    {
        // Fail closed: no configured token must never mean "no authentication".
        var previous = Environment.GetEnvironmentVariable(TokenAuth.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenAuth.EnvVar, null);

            var (token, generated) = TokenAuth.Resolve();

            Assert.True(generated);
            Assert.NotEmpty(token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenAuth.EnvVar, previous);
        }
    }

    // ---- CORS (the shell's origin is not the API's) ------------------------------------------------

    [Fact]
    public async Task A_preflight_from_the_shell_origin_is_answered_without_a_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/session");
        request.Headers.Add("Origin", "http://tauri.localhost");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await _http.SendAsync(request);

        // A preflight carries no Authorization header, so requiring the token here would break the shell.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("http://tauri.localhost", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("http://tauri.localhost")]
    [InlineData("tauri://localhost")]
    [InlineData("http://localhost:5173")]
    public async Task An_allowed_origin_receives_the_cors_header(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", origin);

        using var response = await _http.SendAsync(request);

        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task An_unknown_origin_is_not_granted_cors_access()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await _http.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        // The request itself still succeeds for a non-browser client; CORS is enforced by the browser.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task WaitForHostAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = await _http.GetAsync("/api/settings");
                if (probe.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("the api host never started listening");
    }
}
