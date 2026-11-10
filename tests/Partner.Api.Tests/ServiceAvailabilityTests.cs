using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Api.Services;
using Partner.Core.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Partner.Api.Tests;

/// <summary>
/// Availability guarantees of the management plane: a summary that is waiting on the model must not block
/// <c>GET /api/services</c>, a shutdown must finish inside its budget, and a delete-with-purge must not leave a
/// freshly summarized record behind.
/// </summary>
public sealed class ServiceAvailabilityTests : ApiFixture
{
    protected override string WorkspaceName => "service-availability";

    private static async Task<string> ReadAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync();
    }

    private async Task<(string Id, string Token, int Port)> CreateAsync(int timeoutSeconds = 60)
    {
        var port = FakeUpstream.FreePort();
        var response = await _http.PostAsJsonAsync("/api/services", new
        {
            name = "availability",
            enabled = true,
            port,
            protocol = "openai",
            model = "exposed-model",
            contextTokens = 8000,
            prompt = "",
            recall = new { enabled = false, limit = 8, tokenLimit = 2000 },
            summary = new { enabled = true, intervalMinutes = 1440, maxWaitMinutes = 1440 },
            upstream = new
            {
                inherit = false,
                baseUrl = "http://127.0.0.1:9",
                model = "upstream-model",
                apiKey = "K",
                timeoutSeconds,
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            json.GetProperty("settings").GetProperty("id").GetString()!,
            json.GetProperty("token").GetString()!,
            port);
    }

    [Fact]
    public async Task A_slow_summary_does_not_block_the_management_endpoints()
    {
        // Every summarizer call hangs for five seconds. The flush therefore stays in flight while the list is read.
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            await Task.Delay(TimeSpan.FromSeconds(2));
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"summary\"}}]}");
        });

        var port = FakeUpstream.FreePort();
        var created = await _http.PostAsJsonAsync("/api/services", new
        {
            name = "slow-summary",
            enabled = true,
            port,
            protocol = "openai",
            model = "exposed-model",
            contextTokens = 8000,
            prompt = "",
            recall = new { enabled = false, limit = 8, tokenLimit = 2000 },
            summary = new { enabled = true, intervalMinutes = 1440, maxWaitMinutes = 1440 },
            upstream = new { inherit = false, baseUrl = upstream.BaseUrl, model = "u", apiKey = "K" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("settings").GetProperty("id").GetString()!;
        var token = json.GetProperty("token").GetString()!;

        // One real request, so there is something to summarize.
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "exposed-model",
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var flush = _http.PostAsync($"/api/services/{id}/flush", content: null);
        await Task.Delay(300);

        var watch = Stopwatch.StartNew();
        var list = await _http.GetAsync("/api/services");
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(
            watch.ElapsedMilliseconds < 500,
            $"GET /api/services took {watch.ElapsedMilliseconds} ms while a flush was in flight");

        await flush;
    }

    [Fact]
    public async Task Shutdown_writes_every_instance_within_the_budget()
    {
        // The fake answers a normal completion at once and hangs only on the summarizer's call. The summarizer is the
        // one that sends a system message (the client requests here carry none), and the payload is JSON, so the
        // non-ASCII prompt itself is escaped on the wire — the role is what can be matched.
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            var body = await ReadAsync(ctx);
            if (body.Contains("\"system\"", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var host = _app.Services.GetRequiredService<ServiceHost>();
        host.ShutdownFlushBudget = TimeSpan.FromSeconds(2);

        var created = new List<(string Id, string Token, int Port)>();
        for (var i = 0; i < 3; i++)
        {
            var port = FakeUpstream.FreePort();
            var response = await _http.PostAsJsonAsync("/api/services", new
            {
                name = $"s{i}",
                enabled = true,
                port,
                protocol = "openai",
                model = "exposed-model",
                contextTokens = 8000,
                prompt = "",
                recall = new { enabled = false, limit = 8, tokenLimit = 2000 },
                summary = new { enabled = true, intervalMinutes = 1440, maxWaitMinutes = 1440 },
                upstream = new { inherit = false, baseUrl = upstream.BaseUrl, model = "u", apiKey = "K" },
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            created.Add((
                json.GetProperty("settings").GetProperty("id").GetString()!,
                json.GetProperty("token").GetString()!,
                port));
        }

        foreach (var (_, token, port) in created)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            await client.PostAsJsonAsync("/v1/chat/completions", new
            {
                model = "exposed-model",
                messages = new[] { new { role = "user", content = "question" } },
            });
        }

        var watch = Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25), $"shutdown took {watch.Elapsed}");

        var records = await _context.Memory.ListAsync(
            new MemoryQuery(IncludeInvalidated: true, Limit: int.MaxValue));
        var serviceRecords = records.Where(r => MemoryOrigins.IsService(r.Origin)).ToList();
        Assert.Equal(3, serviceRecords.Count);

        // Out of budget means the template sentence "调用方要求…；服务…。" — the "；服务" join only exists in the
        // fallback. (The captured assistant text appears inside both forms, so it cannot be the marker.)
        Assert.All(serviceRecords, r =>
        {
            Assert.Contains("调用方要求", r.Body, StringComparison.Ordinal);
            Assert.Contains("；服务", r.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Deleting_with_purge_stops_the_listener_before_purging()
    {
        var called = 0;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            Interlocked.Increment(ref called);
            await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"summary\"}}]}");
        });

        var (id, token, port) = await CreateAsync();
        var put = await _http.PutAsJsonAsync($"/api/services/{id}", new
        {
            name = "deleting",
            enabled = true,
            port,
            protocol = "openai",
            model = "exposed-model",
            contextTokens = 8000,
            prompt = "",
            recall = new { enabled = false, limit = 8, tokenLimit = 2000 },
            summary = new { enabled = true, intervalMinutes = 1440, maxWaitMinutes = 1440 },
            upstream = new { inherit = false, baseUrl = upstream.BaseUrl, model = "u", apiKey = "K" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "exposed-model",
            messages = new[] { new { role = "user", content = "remember this" } },
        });

        // Delete with purge. The stop inside it flushes the one round; the purge that follows must therefore see
        // it and remove it, leaving no valid record for this origin.
        var deleted = await _http.DeleteAsync($"/api/services/{id}?purgeMemories=true");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var remaining = await _context.Memory.ListAsync(
            new MemoryQuery(Origin: MemoryOrigins.Service(id), Limit: int.MaxValue));
        Assert.Empty(remaining);

        // And the port is closed: the listener went away with the instance.
        using var after = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        after.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => after.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } }));
    }
}