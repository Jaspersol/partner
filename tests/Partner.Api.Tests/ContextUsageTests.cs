using Partner.Api.Settings;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// The context occupancy the session view reports. It is measured with the same assembler a turn uses, so the
/// number on screen is the number that decides whether history has to be folded.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    private async Task<JsonElement> ContextAsync()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        return view.GetProperty("context");
    }

    [Fact]
    public async Task An_empty_session_reports_the_budget_it_has()
    {
        var context = await ContextAsync();

        Assert.Equal(RuntimeSettings.Default.Model.ContextTokens, context.GetProperty("contextTokens").GetInt32());
        Assert.Equal(RuntimeSettings.Default.Model.MaxTokens, context.GetProperty("maxTokens").GetInt32());

        // The prompt budget reserves room for the completion.
        Assert.Equal(
            RuntimeSettings.Default.Model.ContextTokens - RuntimeSettings.Default.Model.MaxTokens,
            context.GetProperty("budgetTokens").GetInt32());

        Assert.Equal(0, context.GetProperty("keptItems").GetInt32());
        Assert.Equal(0, context.GetProperty("foldedItems").GetInt32());
    }

    [Fact]
    public async Task The_occupancy_grows_with_the_history()
    {
        var before = await ContextAsync();

        for (var i = 0; i < 4; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = $"turn {i} with a few words in it",
            });
        }

        var after = await ContextAsync();

        Assert.True(
            after.GetProperty("usedTokens").GetInt32() > before.GetProperty("usedTokens").GetInt32(),
            $"{before.GetProperty("usedTokens").GetInt32()} -> {after.GetProperty("usedTokens").GetInt32()}");

        Assert.Equal(4, after.GetProperty("keptItems").GetInt32());
        Assert.Equal(0, after.GetProperty("foldedItems").GetInt32());
    }

    [Fact]
    public async Task History_beyond_the_window_is_reported_as_folded()
    {
        for (var i = 0; i < 12; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = new string('x', 400),
            });
        }

        // A window far too small for twelve 100-token entries.
        var put = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { contextTokens = 512, maxTokens = 64 },
        });
        put.EnsureSuccessStatusCode();

        var context = await ContextAsync();

        Assert.True(context.GetProperty("foldedItems").GetInt32() > 0, "nothing was reported as folded");
        Assert.True(context.GetProperty("keptItems").GetInt32() < 12, "everything was kept in a 512-token window");

        // And what is kept fits: the usage never exceeds the budget it reports.
        Assert.True(context.GetProperty("usedTokens").GetInt32() <= context.GetProperty("budgetTokens").GetInt32());
    }

    [Fact]
    public async Task A_folded_history_reports_a_smaller_occupancy_than_the_raw_one()
    {
        for (var i = 0; i < 12; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = new string('y', 400),
            });
        }

        var uncapped = await ContextAsync();

        await _http.PutAsJsonAsync("/api/settings", new { model = new { contextTokens = 512, maxTokens = 64 } });
        var capped = await ContextAsync();

        Assert.True(
            capped.GetProperty("usedTokens").GetInt32() < uncapped.GetProperty("usedTokens").GetInt32(),
            $"{uncapped.GetProperty("usedTokens").GetInt32()} -> {capped.GetProperty("usedTokens").GetInt32()}");
    }
}
