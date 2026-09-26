using Partner.Core.Contracts;
using Partner.Core.Sessions;
using Partner.Core.Agent.Llm;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Partner.Api.Auth;
using Partner.Api.Settings;
using Partner.Api.Voice;

namespace Partner.Api.Tests;

/// <summary>
/// The settings surface: TTS/ASR/model configuration, the write-only API key, and the fact that changing the
/// model actually re-points the consumer rather than only changing what the UI displays.
/// </summary>
public sealed class SettingsEndpointTests : IAsyncLifetime
{
    private string _workspace = string.Empty;
    private ApiContext _context = null!;
    private ConsumerHost _consumer = null!;
    private VoiceHost _voice = null!;
    private WebApplication _app = null!;
    private Task _run = null!;
    private HttpClient _http = null!;
    private int _port;
    private string _token = string.Empty;

    public async Task InitializeAsync()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "partner-settings-tests", Guid.NewGuid().ToString("N"));
        _port = FreePort();
        _token = TokenAuth.Generate();

        var (app, context, consumer, voice) = await ApiHost.BuildAsync(_workspace, _port, _token);
        _app = app;
        _context = context;
        _consumer = consumer;
        _voice = voice;
        _run = _app.RunAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = await _http.GetAsync("/api/settings");
                if (probe.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(50);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _run;
        await _voice.DisposeAsync();
        await _consumer.DisposeAsync();
        await _context.DisposeAsync();

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Shrinking_the_context_folds_the_history_once()
    {
        for (var i = 1; i <= 6; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = $"turn {i}",
            });
        }

        // The consumer is disabled in this fixture (no model), so compaction reports that it cannot summarise
        // rather than silently doing nothing.
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { contextItems = 3 },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();

        var compaction = body.GetProperty("compaction");
        Assert.False(compaction.GetProperty("compacted").GetBoolean());
        Assert.Contains("未配置模型", compaction.GetProperty("reason").GetString()!, StringComparison.Ordinal);

        // Nothing was removed: a failed compaction must not lose history.
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(6, view.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Growing_the_context_does_not_compact()
    {
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "one",
        });

        var put = await _http.PutAsJsonAsync("/api/settings", new { model = new { contextItems = 50 } });
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("compaction").ValueKind);
    }

    [Fact]
    public async Task The_context_and_output_limits_round_trip_and_are_validated()
    {
        var saved = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { contextItems = 12, maxTokens = 2048 },
        });
        saved.EnsureSuccessStatusCode();

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(12, view.GetProperty("model").GetProperty("contextItems").GetInt32());
        Assert.Equal(2048, view.GetProperty("model").GetProperty("maxTokens").GetInt32());

        var bad = await _http.PutAsJsonAsync("/api/settings", new { model = new { contextItems = 0, maxTokens = -1 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(errors, e => e.Contains("contextItems", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("maxTokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_configured_model_makes_compaction_actually_fold_history()
    {
        // Same host, but with a scripted model so the summary has a producer.
        var workspace = Path.Combine(Path.GetTempPath(), "partner-compaction-tests", Guid.NewGuid().ToString("N"));
        var port = FreePort();
        var token = TokenAuth.Generate();

        var llm = new ScriptedLlmClient(new[]
        {
            new LlmResponse("前几条：用户依次打了招呼。", Array.Empty<LlmToolCall>(), "stop"),
        });

        var (app, context, consumer, voice) = await ApiHost.BuildAsync(workspace, port, token, clock: null, llm: llm);
        var run = app.RunAsync();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    using var probe = await http.GetAsync("/api/session");
                    if (probe.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }

                await Task.Delay(50);
            }

            for (var i = 1; i <= 5; i++)
            {
                await context.Session.AppendAsync(new SessionEvent
                {
                    Kind = SessionEventKind.User,
                    Source = InputSource.Text,
                    Body = $"turn {i}",
                });
            }

            var put = await http.PutAsJsonAsync("/api/settings", new { model = new { contextItems = 2 } });
            var compaction = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("compaction");

            Assert.True(compaction.GetProperty("compacted").GetBoolean(), compaction.GetProperty("reason").GetString());
            Assert.Equal(3, compaction.GetProperty("foldedEvents").GetInt32());

            // The view is now the summary plus the two turns the window kept.
            var view = await http.GetFromJsonAsync<JsonElement>("/api/session");
            var bodies = view.GetProperty("items").EnumerateArray()
                .Select(i => (Kind: i.GetProperty("kind").GetString(), Body: i.GetProperty("body").GetString()))
                .ToList();

            Assert.Equal(3, bodies.Count);
            Assert.Contains(bodies, b => b.Kind == "Summary" && b.Body == "前几条：用户依次打了招呼。");
            Assert.Contains(bodies, b => b.Body == "turn 4");
            Assert.Contains(bodies, b => b.Body == "turn 5");
        }
        finally
        {
            await app.StopAsync();
            await run;
            await voice.DisposeAsync();
            await consumer.DisposeAsync();
            await context.DisposeAsync();

            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task The_defaults_cover_every_section()
    {
        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");

        foreach (var section in new[] { "input", "output", "asr", "tts", "model" })
        {
            Assert.True(settings.TryGetProperty(section, out _), $"missing section {section}");
        }

        Assert.Equal(1200, settings.GetProperty("input").GetProperty("utteranceGapMs").GetDouble());
        Assert.Equal(40, settings.GetProperty("input").GetProperty("maxSplitChars").GetInt32());
        Assert.Equal(24000, settings.GetProperty("output").GetProperty("sampleRate").GetInt32());
        Assert.Equal("qwen3-asr", settings.GetProperty("asr").GetProperty("model").GetString());
        Assert.Equal("zh", settings.GetProperty("tts").GetProperty("lang").GetString());
        Assert.Equal(120, settings.GetProperty("model").GetProperty("timeoutSeconds").GetDouble());
    }

    [Fact]
    public async Task The_api_key_is_accepted_but_never_echoed()
    {
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", apiKey = "super-secret" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var raw = await _http.GetStringAsync("/api/settings");

        // The endpoint is reachable by anything holding the token, so a key echoed back is a key leaked.
        Assert.DoesNotContain("super-secret", raw, StringComparison.Ordinal);

        var settings = JsonDocument.Parse(raw).RootElement;
        Assert.True(settings.GetProperty("model").GetProperty("apiKeySet").GetBoolean());
        Assert.False(settings.GetProperty("model").TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task An_absent_key_is_kept_and_an_empty_one_clears_it()
    {
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", apiKey = "keep-me" },
        });

        // A client editing an unrelated field must not silently wipe the key.
        await _http.PutAsJsonAsync("/api/settings", new
        {
            input = new { utteranceGapMs = 900 },
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
        });

        var kept = new SettingsStore(_context.Settings.Path).Load();
        Assert.Equal("keep-me", kept.Model.ApiKey);

        // An explicit empty string is how a client says "remove it".
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", apiKey = "" },
        });

        var cleared = new SettingsStore(_context.Settings.Path).Load();
        Assert.Null(cleared.Model.ApiKey);

        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(settings.GetProperty("model").GetProperty("apiKeySet").GetBoolean());
    }

    [Fact]
    public async Task An_unusable_endpoint_is_rejected_with_every_reason()
    {
        var bad = await _http.PutAsJsonAsync("/api/settings", new
        {
            asr = new { baseUrl = "not-a-url" },
            tts = new { baseUrl = "ftp://example.test" },
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();

        // Every problem is reported at once, so a client can fix them in one pass.
        Assert.Contains(errors, e => e.Contains("asr.baseUrl", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("tts.baseUrl", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("model.model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configuring_the_model_starts_the_consumer_without_a_restart()
    {
        var before = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        Assert.False(before.GetProperty("running").GetBoolean());

        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "settings-model" },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        var state = body.GetProperty("consumer");

        Assert.True(state.GetProperty("running").GetBoolean());
        Assert.Equal("settings-model", state.GetProperty("model").GetString());
        Assert.Contains("设置", state.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delivered_item_is_executed_against_the_configured_endpoint()
    {
        // Port 9 is the discard port: the connection is refused, which is exactly the observable we want —
        // the failure proves the request went to the endpoint from settings and not somewhere else.
        var closed = FreePort();
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = $"http://127.0.0.1:{closed}/v1", model = "settings-model" },
        });

        var submitted = await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "hello" });
        var itemId = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

        var failed = await Eventually.IsTrueAsync(async () =>
        {
            var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
            return queue.GetProperty("items").EnumerateArray().Any(item =>
                item.GetProperty("itemId").GetString() == itemId
                && item.GetProperty("status").GetString() == "Failed");
        });

        Assert.True(failed, "the consumer did not run the item against the configured endpoint");

        var queueAfter = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        var error = queueAfter.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("itemId").GetString() == itemId)
            .GetProperty("error").GetString();

        Assert.Contains(closed.ToString(), error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_an_unrelated_section_does_not_restart_the_consumer()
    {
        var closed = FreePort();
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = $"http://127.0.0.1:{closed}/v1", model = "settings-model" },
        });

        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "one" });

        var handled = await Eventually.IsTrueAsync(async () =>
        {
            var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
            return state.GetProperty("stats").GetProperty("handled").GetInt32() == 1;
        });
        Assert.True(handled, "the first item was never handled");

        // A restart would reset the counters; editing the voice section must not interrupt a turn in flight.
        await _http.PutAsJsonAsync("/api/settings", new
        {
            input = new { utteranceGapMs = 700 },
            model = new { baseUrl = $"http://127.0.0.1:{closed}/v1", model = "settings-model" },
        });

        var after = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        Assert.Equal(1, after.GetProperty("stats").GetProperty("handled").GetInt32());
        Assert.True(after.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Settings_win_over_the_environment()
    {
        var previousUrl = Environment.GetEnvironmentVariable(HttpLlmClient.BaseUrlVariable);
        var previousModel = Environment.GetEnvironmentVariable(HttpLlmClient.ModelVariable);

        try
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, "http://127.0.0.1:9/v1");
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, "env-model");

            await _http.PutAsJsonAsync("/api/settings", new
            {
                model = new { baseUrl = "http://127.0.0.1:9/v1", model = "settings-model" },
            });

            var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");

            Assert.Equal("settings-model", state.GetProperty("model").GetString());
            Assert.Contains("设置", state.GetProperty("reason").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, previousUrl);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, previousModel);
        }
    }
}
