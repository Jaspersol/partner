using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Partner.Api.Auth;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Tests;

/// <summary>
/// A model that thinks out loud, seen from the session view.
/// <para>
/// Its own fixture, and deliberately so. <see cref="ConsumerEndpointTests"/> injects a turn that parks on a
/// gate and only lets go mid-assertion, which is how a test watches a live row. Reconfiguring a consumer whose
/// turn is still in flight blocks, and because the release happens <i>inside</i> the blocked test it would
/// deadlock: the test waits for the reconfigure, the reconfigure waits for a gate the test has not reached.
/// One consumer, one turn at a time, and this fixture only ever has one in flight — so a client that returns
/// on its own is enough and the wait is a poll rather than a gate.
/// </para>
/// </summary>
public sealed class ReasoningStreamTests : ApiFixture
{
    private readonly ReasoningLlm _llm = new();

    protected override string WorkspaceName => "partner-reasoning-tests";

    protected override ILlmClient Llm => _llm;

    /// <summary>Answers at once, after announcing a stretch of thinking on its own channel.</summary>
    private sealed class ReasoningLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse(
                "下午三点",
                Array.Empty<LlmToolCall>(),
                "stop",
                Reasoning: "先确认时区再换算"));

        public async Task<LlmResponse> StreamWithReasoningAsync(
            LlmRequest request,
            Action<string> onText,
            Action<string> onReasoning,
            CancellationToken ct = default)
        {
            onReasoning("先确认时区");
            onReasoning("再换算");
            onText("下午三点");
            await Task.Yield();

            return new LlmResponse(
                "下午三点",
                Array.Empty<LlmToolCall>(),
                "stop",
                Reasoning: "先确认时区再换算");
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
    public async Task Reasoning_lands_in_the_session_as_its_own_row()
    {
        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "几点了" });

        var settled = await WaitForSessionAsync(session =>
            !session.GetProperty("busy").GetBoolean()
            && session.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "Assistant"));

        var thinking = settled.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "Thinking");

        // Recorded, not merely shown while it happened: a thinking row that vanished the moment the answer
        // arrived would leave "what was it working out" unanswerable without re-asking the model.
        Assert.Equal("先确认时区再换算", thinking.GetProperty("body").GetString());

        // The answer and the reasoning are two different things, and only one of them is a conversation turn.
        Assert.Equal(
            "下午三点",
            settled.GetProperty("items").EnumerateArray()
                .First(item => item.GetProperty("kind").GetString() == "Assistant")
                .GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_live_reasoning_row_is_gone_once_the_answer_lands()
    {
        // The finished Thinking event replaces it. Leaving both would show the same reasoning twice — once as
        // "what it is doing now" and once as history — for as long as the turn is over.
        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "几点了" });

        var settled = await WaitForSessionAsync(session =>
            !session.GetProperty("busy").GetBoolean()
            && session.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "Assistant"));

        Assert.DoesNotContain(
            settled.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("streaming").GetBoolean());
    }
}
