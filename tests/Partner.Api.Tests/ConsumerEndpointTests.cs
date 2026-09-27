using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Api.Auth;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Tests;

/// <summary>
/// The queue consumer as the host wires it (design doc §2.2). The interesting claims are negative ones:
/// it must not claim to be running when no model is configured, and it must not leave work queued forever
/// when one is.
/// </summary>
public sealed class ConsumerEndpointTests : IAsyncLifetime
{
    private string _workspace = string.Empty;
    private ApiContext _context = null!;
    private ConsumerHost _consumer = null!;
    private VoiceHost _voice = null!;
    private WebApplication _app = null!;
    private Task _run = null!;
    private HttpClient _http = null!;
    private string _token = string.Empty;
    private int _port;

    private static readonly string[] Scripted =
    {
        """{"choices":[{"message":{"content":"书房的灯已经打开了。"},"finish_reason":"stop"}]}""",
    };

    public async Task InitializeAsync()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "partner-consumer-tests", Guid.NewGuid().ToString("N"));
        _port = FreePort();
        _token = TokenAuth.Generate();

        var llm = new ScriptedLlmClient(new[]
        {
            new LlmResponse("书房的灯已经打开了。", Array.Empty<LlmToolCall>(), "stop"),
        });

        var (app, context, consumer, voice) = await ApiHost.BuildAsync(_workspace, _port, _token, clock: null, llm: llm);
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
                using var probe = await _http.GetAsync("/api/consumer");
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

    /// <summary>Streams two pieces with a gate between them, so a test can look at the session mid-turn.</summary>
    private sealed class GatedStreamingLlm(string first, string second) : ILlmClient
    {
        public TaskCompletionSource Release { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("this client only streams");

        public async Task<LlmResponse> StreamAsync(
            LlmRequest request,
            Action<string> onText,
            CancellationToken ct = default)
        {
            onText(first);
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            onText(second);
            return new LlmResponse(first + second, Array.Empty<LlmToolCall>(), "stop");
        }
    }

    private async Task<JsonElement> WaitForSessionAsync(Func<JsonElement, bool> ready)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var session = await _http.GetFromJsonAsync<JsonElement>("/api/session");
            if (ready(session))
            {
                return session;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("the session never reached the expected state");
    }

    [Fact]
    public async Task The_session_shows_the_reply_while_it_is_being_written()
    {
        var llm = new GatedStreamingLlm("正在", "回复");
        await _consumer.ReconfigureAsync(config: null, reason: "streaming test", injected: llm);

        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "写点什么" });

        // Mid-turn: the partial text is on the wire, flagged as streaming, and the view says it is busy. It is
        // not in the log — the log only ever holds the finished answer.
        var live = await WaitForSessionAsync(session => session.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("streaming").GetBoolean()));

        Assert.True(live.GetProperty("busy").GetBoolean());
        var streaming = live.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("streaming").GetBoolean());
        Assert.Equal("正在", streaming.GetProperty("body").GetString());
        Assert.Equal(0, streaming.GetProperty("seq").GetInt64());
        Assert.Equal("AssistantStreaming", streaming.GetProperty("kind").GetString());

        llm.Release.SetResult();

        // After: the live row is gone, and the complete answer is an ordinary event.
        var settled = await WaitForSessionAsync(session =>
            !session.GetProperty("busy").GetBoolean()
            && session.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "Assistant"));

        Assert.DoesNotContain(
            settled.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("streaming").GetBoolean());
        var assistant = settled.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "Assistant");
        Assert.Equal("正在回复", assistant.GetProperty("body").GetString());
    }

    [Fact]
    public async Task With_a_model_the_consumer_reports_running()
    {
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");

        Assert.True(state.GetProperty("running").GetBoolean());
        Assert.Equal("ScriptedLlmClient", state.GetProperty("model").GetString());
        Assert.Equal(0, state.GetProperty("stats").GetProperty("handled").GetInt32());
    }

    [Fact]
    public async Task Delivered_input_is_turned_and_lands_in_the_session()
    {
        var submitted = await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "打开书房的灯" });
        submitted.EnsureSuccessStatusCode();
        var itemId = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

        Assert.True(
            await Eventually.IsTrueAsync(async () => await StatusAsync(itemId!) == "Done"),
            "the consumer never finished the delivered item");

        // The whole point of the consumer: the queue drains and the conversation appears.
        var session = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var bodies = session.GetProperty("items").EnumerateArray()
            .Select(item => (Kind: item.GetProperty("kind").GetString(), Body: item.GetProperty("body").GetString()))
            .ToList();

        Assert.Contains(bodies, entry => entry.Kind == "User" && entry.Body == "打开书房的灯");
        Assert.Contains(bodies, entry => entry.Kind == "Assistant" && entry.Body == "书房的灯已经打开了。");

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Equal(0, queue.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Cancelling_a_queued_item_keeps_it_out_of_the_turn()
    {
        // The consumer is busy with the first item, so the second can be cancelled while it waits.
        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "first" });
        var second = await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "second" });
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itemId").GetString();

        using var cancel = await _http.PostAsync($"/api/queue/{secondId}/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        Assert.True(
            await Eventually.IsTrueAsync(async () => await StatusAsync(secondId!) == "Canceled"),
            "the cancelled item was not settled as canceled");

        var session = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var userBodies = session.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "User")
            .Select(item => item.GetProperty("body").GetString())
            .ToList();

        Assert.DoesNotContain("second", userBodies);
    }

    private async Task<string?> StatusAsync(string itemId)
    {
        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        foreach (var item in queue.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("itemId").GetString() == itemId)
            {
                return item.GetProperty("status").GetString();
            }
        }

        return null;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Without a model there must be no consumer at all — and the reason must be visible, not silent.
/// </summary>
public sealed class UnconfiguredConsumerTests
{
    [Fact]
    public async Task Without_a_model_the_consumer_is_disabled_and_says_why()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "partner-consumer-tests", Guid.NewGuid().ToString("N"));
        var port = FreePort();
        var token = TokenAuth.Generate();

        var previousUrl = Environment.GetEnvironmentVariable(HttpLlmClient.BaseUrlVariable);
        var previousModel = Environment.GetEnvironmentVariable(HttpLlmClient.ModelVariable);

        try
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, null);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, null);

            var (app, context, consumer, voice) = await ApiHost.BuildAsync(workspace, port, token);
            var run = app.RunAsync();

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    using var probe = await http.GetAsync("/api/consumer");
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

            var state = await http.GetFromJsonAsync<JsonElement>("/api/consumer");
            Assert.False(state.GetProperty("running").GetBoolean());

            var reason = state.GetProperty("reason").GetString();
            Assert.Contains(HttpLlmClient.BaseUrlVariable, reason!, StringComparison.Ordinal);

            // Work still lands in the queue; it simply does not move, and the state says so.
            var submitted = await http.PostAsJsonAsync("/api/input", new { source = "text", payload = "orphan" });
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

            var queue = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            Assert.Equal(1, queue.GetProperty("depth").GetInt32());
            Assert.Equal(0, state.GetProperty("stats").GetProperty("handled").GetInt32());

            await app.StopAsync();
            await run;
            await voice.DisposeAsync();
            await consumer.DisposeAsync();
            await context.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, previousUrl);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, previousModel);

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
}

/// <summary>Waits on a state only the running consumer can reach.</summary>
internal static class Eventually
{
    public static async Task<bool> IsTrueAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return await condition().ConfigureAwait(false);
    }
}
