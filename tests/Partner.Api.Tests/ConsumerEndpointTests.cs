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
public sealed class ConsumerEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-consumer-tests";

    protected override ILlmClient Llm => new ScriptedLlmClient(new[]
    {
        new LlmResponse("书房的灯已经打开了。", Array.Empty<LlmToolCall>(), "stop"),
    });

    private static readonly string[] Scripted =
    {
        """{"choices":[{"message":{"content":"书房的灯已经打开了。"},"finish_reason":"stop"}]}""",
    };

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

        try
        {
            await _consumer.ReconfigureAsync(config: null, reason: "streaming test", injected: llm);

            await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "写点什么" });

            // Mid-turn: the partial text is on the wire, flagged as streaming, and the view says it is busy.
            // It is not in the log — the log only ever holds the finished answer.
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
        finally
        {
            // The turn is parked on this gate; a throw before releasing it would hang the class, not fail it.
            llm.Release.TrySetResult();
        }
    }

    // The reasoning counterpart lives in ReasoningStreamTests: reconfiguring this consumer while a gated turn
    // is still in flight deadlocks, because the gate is only released by the test that is itself waiting.

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
            await Eventually.IsTrueAsync(async () => await HasLeftTheQueueAsync(itemId!)),
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
            await Eventually.IsTrueAsync(async () => await HasLeftTheQueueAsync(secondId!)),
            "the cancelled item was not settled as canceled");

        var session = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var userBodies = session.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "User")
            .Select(item => item.GetProperty("body").GetString())
            .ToList();

        Assert.DoesNotContain("second", userBodies);
    }

    /// <summary>
    /// True once the queue no longer holds the item — which is how a finished item now announces itself.
    /// </summary>
    /// <remarks>
    /// It used to report a status, and polled for "Done". It cannot any more: the queue is what is still in
    /// play, so a settled item is absent rather than marked. That is a stronger statement than "Done" — there
    /// is no way for it to still be waiting — but it also means <c>/api/queue</c> no longer answers "what
    /// happened to item X", and anything that needs that has to read the session log or keep its own record.
    /// </remarks>
    private async Task<bool> HasLeftTheQueueAsync(string itemId)
    {
        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        return !queue.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("itemId").GetString() == itemId);
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
        var port = ApiFixture.FreePort();
        var token = TokenAuth.Generate();

        var previousUrl = Environment.GetEnvironmentVariable(HttpLlmClient.BaseUrlVariable);
        var previousModel = Environment.GetEnvironmentVariable(HttpLlmClient.ModelVariable);

        try
        {
            Environment.SetEnvironmentVariable(HttpLlmClient.BaseUrlVariable, null);
            Environment.SetEnvironmentVariable(HttpLlmClient.ModelVariable, null);

            var (app, context, consumer, voice, _) = await ApiHost.BuildAsync(workspace, port, token);
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

}

/// <summary>Waits on a state only the running consumer can reach.</summary>
internal static class Eventually
{
    /// <param name="attempts">
    /// 200 polls at 25ms is 5s, which is enough for a turn that fails against a refused endpoint. It is not
    /// enough when the same machine is also running every other test assembly: give a caller that waits on a
    /// reconfigure *and* a completed turn more room rather than making every caller wait longer.
    /// </param>
    public static async Task<bool> IsTrueAsync(Func<Task<bool>> condition, int attempts = 200)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
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
