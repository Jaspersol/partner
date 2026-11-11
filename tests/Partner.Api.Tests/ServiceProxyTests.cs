using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Partner.Core.Memory;

namespace Partner.Api.Tests;

/// <summary>
/// End-to-end pass-through: a real service listener against a local fake upstream. Covers auth, model mapping,
/// lossless forwarding, memory injection, streaming, and round de-duplication.
/// </summary>
public sealed class ServiceProxyTests : ApiFixture
{
    protected override string WorkspaceName => "service-proxy";

    private async Task<(string Id, string Token, int Port)> CreateAsync(
        string protocol,
        string upstreamBase,
        string upstreamModel,
        string upstreamKey,
        bool recall = false,
        string prompt = "",
        bool summary = false,
        int intervalMinutes = 1440,
        int maxWaitMinutes = 1440)
    {
        var port = FakeUpstream.FreePort();
        var body = new
        {
            name = "proxy-test",
            enabled = true,
            port,
            protocol,
            model = "exposed-model",
            contextTokens = 8000,
            prompt,
            recall = new { enabled = recall, limit = 8, tokenLimit = 2000 },
            summary = new { enabled = summary, intervalMinutes, maxWaitMinutes },
            upstream = new { inherit = false, baseUrl = upstreamBase, model = upstreamModel, apiKey = upstreamKey },
        };

        var response = await _http.PostAsJsonAsync("/api/services", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            json.GetProperty("settings").GetProperty("id").GetString()!,
            json.GetProperty("token").GetString()!,
            port);
    }

    private static HttpClient ServiceClient(int port, string? token = null, bool useApiKey = false)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        if (token is not null)
        {
            if (useApiKey)
            {
                client.DefaultRequestHeaders.Add("x-api-key", token);
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        return client;
    }

    private static async Task<string> ReadAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Auth_and_model_are_swapped_and_the_body_passes_through_unchanged()
    {
        string? seenAuth = null;
        string? seenBody = null;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            seenAuth = ctx.Request.Headers.Authorization.ToString();
            seenBody = await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\","
                + "\"tool_calls\":[{\"function\":{\"name\":\"do_thing\"}}]}}]}");
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");

        using var client = ServiceClient(port, token);
        var payload = new
        {
            model = "exposed-model",
            messages = new[] { new { role = "user", content = "hi" } },
            tools = new[] { new { type = "function", function = new { name = "do_thing" } } },
            tool_choice = "auto",
            response_format = new { type = "json_object" },
        };
        var response = await client.PostAsJsonAsync("/v1/chat/completions", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer UPSTREAM-KEY", seenAuth); // the client's token is not forwarded
        var seen = JsonDocument.Parse(seenBody!).RootElement;
        Assert.Equal("upstream-model", seen.GetProperty("model").GetString());
        Assert.True(seen.TryGetProperty("tools", out _));
        Assert.Equal("auto", seen.GetProperty("tool_choice").GetString());
        Assert.True(seen.TryGetProperty("response_format", out _));

        var back = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("do_thing", back.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Bearer_and_x_api_key_both_work_but_a_wrong_token_is_rejected()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        var payload = new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } };

        using var wrong = ServiceClient(port, "not-the-token");
        var denied = await wrong.PostAsJsonAsync("/v1/chat/completions", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var viaKey = ServiceClient(port, token, useApiKey: true);
        var accepted = await viaKey.PostAsJsonAsync("/v1/chat/completions", payload);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task A_mismatched_model_is_a_404()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        using var client = ServiceClient(port, token);
        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "wrong", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Recall_is_prepended_to_the_last_text_user_and_a_custom_prompt_is_applied()
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "pref-1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "alpha beta the user prefers dark mode",
        });

        string? firstBody = null;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            firstBody ??= await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", recall: true);
        using var client = ServiceClient(port, token);
        var payload = new
        {
            model = "exposed-model",
            messages = new[] { new { role = "user", content = "alpha beta" } },
        };
        await client.PostAsJsonAsync("/v1/chat/completions", payload);

        var body = JsonDocument.Parse(firstBody!).RootElement;
        var messages = body.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString()); // no system when Prompt is empty
        var content = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("以下是背景资料", content);
        Assert.Contains("the user prefers dark mode", content);
    }

    [Fact]
    public async Task A_custom_prompt_is_inserted_as_a_system_message()
    {
        string? seen = null;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            seen ??= await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var (_, token, port) = await CreateAsync(
            "openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", prompt: "CUSTOM SYSTEM PROMPT");
        using var client = ServiceClient(port, token);
        await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } });

        var messages = JsonDocument.Parse(seen!).RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("CUSTOM SYSTEM PROMPT", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SSE_frames_reach_the_client_before_the_upstream_ends()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"FIRST\"}}]}\n\n");
            await ctx.Response.Body.FlushAsync();
            await Task.Delay(1500);
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        using var client = ServiceClient(port, token);
        client.Timeout = TimeSpan.FromSeconds(10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "exposed-model",
                stream = true,
                messages = new[] { new { role = "user", content = "hi" } },
            }),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Contains("FIRST", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task Tool_result_only_user_is_skipped_and_resent_history_is_one_round()
    {
        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "topic-1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "alpha beta topic the user likes this topic",
        });

        string? seen = null;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            seen ??= await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"content\":[{\"type\":\"text\",\"text\":\"answer\"}]}");
        });

        var (id, token, port) = await CreateAsync(
            "anthropic", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", recall: true, summary: true);
        using var client = ServiceClient(port, token, useApiKey: true);

        var payload = new
        {
            model = "exposed-model",
            max_tokens = 100,
            messages = new object[]
            {
                new { role = "user", content = "alpha beta topic" },
                new { role = "assistant", content = new[] { new { type = "tool_use", name = "do_thing" } } },
                new { role = "user", content = new[] { new { type = "tool_result", content = "done" } } },
            },
        };

        // The same history is resent on each step of the tool loop: it must stay one pending round.
        await client.PostAsJsonAsync("/v1/messages", payload);
        await client.PostAsJsonAsync("/v1/messages", payload);

        var messages = JsonDocument.Parse(seen!).RootElement.GetProperty("messages");
        // The injection goes into the *text* user at index 0; the tool_result user at index 2 is left alone.
        Assert.Contains("以下是背景资料", messages[0].GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Array, messages[2].GetProperty("content").ValueKind);

        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Equal(1, state.GetProperty("state").GetProperty("pendingRounds").GetInt32());
    }

    [Fact]
    public async Task A_service_with_no_token_rejects_an_empty_bearer_and_a_bare_request()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        // Write the instance straight to the store with an empty token: the endpoint would generate one, and the
        // point here is the gate, not the creation path.
        var port = FakeUpstream.FreePort();
        var stored = _context.Settings.Load();
        _context.Settings.Save(stored with
        {
            Services = new Partner.Api.Settings.ServicesSettings
            {
                Instances = new[]
                {
                    new Partner.Api.Settings.ServiceSettings
                    {
                        Id = "svc",
                        Name = "no-token",
                        Port = port,
                        Protocol = "openai",
                        Model = "exposed-model",
                        ContextTokens = 8000,
                        Token = string.Empty,
                        Upstream = new Partner.Api.Settings.ServiceUpstreamSettings
                        {
                            Inherit = false,
                            BaseUrl = upstream.BaseUrl,
                            Model = "upstream-model",
                            ApiKey = "K",
                        },
                    },
                },
            },
        });

        var host = _app.Services.GetRequiredService<Partner.Api.Services.ServiceHost>();
        await host.ReconcileAsync(new Partner.Api.Settings.ServicesSettings(), stored.Services with
        {
            Instances = Array.Empty<Partner.Api.Settings.ServiceSettings>(),
        }, default);

        await Task.Delay(50);

        var different = _context.Settings.Load();
        await host.ReconcileAsync(different.Services with { Instances = Array.Empty<Partner.Api.Settings.ServiceSettings>() },
            different.Services, default);

        var payload = new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } };

        using var bare = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        bare.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        var emptyBearer = await bare.PostAsJsonAsync("/v1/chat/completions", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, emptyBearer.StatusCode);

        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var noHeader = await anonymous.PostAsJsonAsync("/v1/chat/completions", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);
    }

    [Fact]
    public async Task A_settings_put_that_adds_an_instance_without_a_token_generates_one()
    {
        var port = FakeUpstream.FreePort();
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            services = new
            {
                instances = new[]
                {
                    new
                    {
                        id = "put-svc",
                        name = "from-put",
                        enabled = false,
                        port,
                        protocol = "openai",
                        model = "m",
                        contextTokens = 8000,
                        prompt = "",
                    },
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var one = await _http.GetFromJsonAsync<JsonElement>("/api/services/put-svc");
        Assert.True(one.GetProperty("settings").GetProperty("tokenSet").GetBoolean());
    }

    [Fact]
    public async Task An_upstream_whose_protocol_differs_from_the_service_is_refused_at_save_time()
    {
        var port = FakeUpstream.FreePort();
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9", model = "m", protocol = "anthropic", apiKey = "k" },
            services = new
            {
                instances = new[]
                {
                    new
                    {
                        id = "mismatch",
                        name = "mismatch",
                        enabled = false,
                        port,
                        protocol = "openai",
                        model = "m",
                        contextTokens = 8000,
                        prompt = "",
                    },
                },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("不一致", body.GetProperty("errors").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_protocol_mismatch_at_request_time_is_a_502_and_never_reaches_the_upstream()
    {
        var called = 0;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            called++;
            await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var (id, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        using var client = ServiceClient(port, token);

        // The state the validator would have refused, reached the way the finding describes: a hand-edited file.
        // Save() validates, so the JSON is written directly and the running host is reconciled by hand.
        var path = _context.Settings.Path;
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["model"] = new JsonObject
        {
            ["baseUrl"] = upstream.BaseUrl,
            ["model"] = "upstream-model",
            ["apiKey"] = "k",
            ["protocol"] = "anthropic",
        };
        var instance = document["services"]!["instances"]!.AsArray()[0]!.AsObject();
        instance["upstream"] = new JsonObject { ["inherit"] = true };
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var host = _app.Services.GetRequiredService<Partner.Api.Services.ServiceHost>();
        await host.ReconcileAsync(new Partner.Api.Settings.ServicesSettings(), _context.Settings.Load().Services, default);

        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, called);

        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Contains(
            "does not match",
            state.GetProperty("state").GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chinese_character_split_across_chunks_reaches_the_summary_intact()
    {
        string? body = null;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            body ??= await ReadAsync(ctx);
            ctx.Response.ContentType = "text/event-stream";
            var frame = "data: {\"choices\":[{\"delta\":{\"content\":\"中文摘要\"}}]}\n\n"u8.ToArray();

            // One byte per write: every multi-byte character is split across chunks.
            foreach (var b in frame)
            {
                await ctx.Response.Body.WriteAsync(new[] { b });
                await ctx.Response.Body.FlushAsync();
            }

            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
        });

        var (id, token, port) = await CreateAsync(
            "openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", summary: true);
        using var client = ServiceClient(port, token);
        client.Timeout = TimeSpan.FromSeconds(20);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "exposed-model",
            stream = true,
            messages = new[] { new { role = "user", content = "请给个摘要" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The client still gets the whole stream, and the captured text has no replacement characters.
        var streamed = await response.Content.ReadAsStringAsync();
        Assert.Contains("[DONE]", streamed, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', streamed);

        var flush = await _http.PostAsync($"/api/services/{id}/flush", content: null);
        Assert.Equal(HttpStatusCode.OK, flush.StatusCode);
        var memoryId = (await flush.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("memoryId").GetString();
        var record = await _context.Memory.GetAsync(memoryId!);
        Assert.Contains("中文摘要", record!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_json_sse_data_line_does_not_break_the_stream()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n");
            await ctx.Response.WriteAsync("data: ping\n\n");
            await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"second\"}}]}\n\n");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        using var client = ServiceClient(port, token);
        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "exposed-model",
            stream = true,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("[DONE]", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_usage_frame_with_empty_choices_does_not_abort_a_captured_stream()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n");
            await ctx.Response.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n\n");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
        });

        var (id, token, port) = await CreateAsync(
            "openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", summary: true);
        using var client = ServiceClient(port, token);
        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "exposed-model",
            stream = true,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("[DONE]", text, StringComparison.Ordinal);

        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Equal(1, state.GetProperty("state").GetProperty("pendingRounds").GetInt32());
    }

    [Fact]
    public async Task An_html_error_page_is_forwarded_verbatim_and_not_recorded()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync("<html><body>bad gateway</body></html>");
        });

        var (id, token, port) = await CreateAsync(
            "openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", summary: true);
        using var client = ServiceClient(port, token);

        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("bad gateway", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Equal(0, state.GetProperty("state").GetProperty("pendingRounds").GetInt32());
    }

    [Fact]
    public async Task A_slow_upstream_times_out_with_504()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            await Task.Delay(TimeSpan.FromSeconds(30));
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[]}");
        });

        var port = FakeUpstream.FreePort();
        var (_, token, _) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        var created = await _http.GetFromJsonAsync<JsonElement>($"/api/services");
        var id = created[0].GetProperty("settings").GetProperty("id").GetString()!;

        var put = await _http.PutAsJsonAsync($"/api/services/{id}", new
        {
            name = "proxy-test",
            enabled = true,
            port,
            protocol = "openai",
            model = "exposed-model",
            contextTokens = 8000,
            prompt = "",
            recall = new { enabled = false, limit = 8, tokenLimit = 2000 },
            summary = new { enabled = false, intervalMinutes = 1440, maxWaitMinutes = 1440 },
            upstream = new { inherit = false, baseUrl = upstream.BaseUrl, model = "upstream-model", apiKey = "K", timeoutSeconds = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var client = ServiceClient(port, token);
        client.Timeout = TimeSpan.FromSeconds(20);
        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_headers_reach_the_client_but_cookies_do_not()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.StatusCode = 429;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["retry-after"] = "3";
            ctx.Response.Headers["x-ratelimit-remaining-requests"] = "0";
            ctx.Response.Headers["request-id"] = "req-1";
            ctx.Response.Headers["set-cookie"] = "session=secret";
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"slow down\"}}");
        });

        var (_, token, port) = await CreateAsync("openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY");
        using var client = ServiceClient(port, token);

        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "exposed-model", messages = new[] { new { role = "user", content = "hi" } } });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("3", response.Headers.GetValues("retry-after").Single());
        Assert.Equal("0", response.Headers.GetValues("x-ratelimit-remaining-requests").Single());
        Assert.Equal("req-1", response.Headers.GetValues("request-id").Single());
        Assert.False(response.Headers.Contains("set-cookie"));
    }

    [Fact]
    public async Task Different_histories_with_the_same_last_message_are_two_rounds()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            await ReadAsync(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        });

        var (id, token, port) = await CreateAsync(
            "openai", upstream.BaseUrl, "upstream-model", "UPSTREAM-KEY", summary: true);
        using var client = ServiceClient(port, token);

        foreach (var earlier in new[] { "first conversation", "second conversation" })
        {
            await client.PostAsJsonAsync("/v1/chat/completions", new
            {
                model = "exposed-model",
                messages = new object[]
                {
                    new { role = "user", content = earlier },
                    new { role = "assistant", content = "ok" },
                    new { role = "user", content = "你好" },
                },
            });
        }

        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/services/{id}");
        Assert.Equal(2, state.GetProperty("state").GetProperty("pendingRounds").GetInt32());
    }
}
