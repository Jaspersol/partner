using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>A history that has outgrown the window is folded after a turn, without anyone asking.</summary>
public sealed class AutoCompactionTests : ApiFixture
{
    protected override string WorkspaceName => "partner-auto-compaction-tests";

    // First call answers the turn; the second is the summary the compaction asks the same model for.
    protected override ILlmClient Llm => new ScriptedLlmClient(new[]
    {
        new LlmResponse("好的", Array.Empty<LlmToolCall>(), "stop"),
        new LlmResponse("前面的对话：用户反复问候。", Array.Empty<LlmToolCall>(), "stop"),
    });

    [Fact]
    public async Task A_finished_turn_over_a_history_that_no_longer_fits_leaves_a_summary_behind()
    {
        for (var i = 0; i < 12; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = $"earlier message number {i} " + new string('x', 120),
            });
        }

        // A window that holds the system block and a couple of those messages, no more.
        var system = (await _consumer.MeasureContextAsync(_context.Settings.Load())).SystemTokens;
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { contextTokens = system + 8 + 120, maxTokens = 8 },
        });
        put.EnsureSuccessStatusCode();

        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "hello" });

        var folded = await Eventually.IsTrueAsync(async () =>
        {
            var session = await _http.GetFromJsonAsync<JsonElement>("/api/session");
            return session.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("kind").GetString() == "Summary");
        });

        Assert.True(folded, "no summary appeared after the turn");

        var usage = await _consumer.MeasureContextAsync(_context.Settings.Load());
        Assert.Equal(0, usage.FoldedItems);
    }
}
