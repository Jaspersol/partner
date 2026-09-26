using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// Removing history from the session view. The two operations differ only in range: delete one event, or
/// everything from the selected one onward.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    private async Task<List<long>> VisibleSeqsAsync()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        return view.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("seq").GetInt64()).ToList();
    }

    private async Task<long> SeedAsync(string body)
    {
        return await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = body,
        });
    }

    [Fact]
    public async Task Deleting_one_event_leaves_every_later_event_alone()
    {
        await SeedAsync("one");
        var second = await SeedAsync("two");
        await SeedAsync("three");

        var response = await _http.PostAsync($"/api/session/{second}/delete", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var visible = await VisibleSeqsAsync();
        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(second, visible);

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var bodies = view.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("body").GetString()).ToList();
        Assert.Equal(new[] { "one", "three" }, bodies);
    }

    [Fact]
    public async Task Retracting_removes_the_selected_event_and_everything_after_it()
    {
        await SeedAsync("one");
        var second = await SeedAsync("two");
        await SeedAsync("three");
        await SeedAsync("four");

        var response = await _http.PostAsync($"/api/session/{second}/retract", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(second, body.GetProperty("removedFrom").GetInt64());
        Assert.True(body.GetProperty("removedTo").GetInt64() >= second);

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var bodies = view.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("body").GetString()).ToList();
        Assert.Equal(new[] { "one" }, bodies);
    }

    [Fact]
    public async Task Events_appended_after_a_retraction_stay_visible()
    {
        var first = await SeedAsync("one");
        await SeedAsync("two");

        await _http.PostAsync($"/api/session/{first}/retract", content: null);
        Assert.Empty(await VisibleSeqsAsync());

        // The point of closing the range: the conversation continues instead of being swallowed forever.
        await SeedAsync("three");

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var bodies = view.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("body").GetString()).ToList();
        Assert.Equal(new[] { "three" }, bodies);
    }

    [Fact]
    public async Task Removing_an_event_that_is_not_visible_is_refused()
    {
        await SeedAsync("one");
        var second = await SeedAsync("two");

        await _http.PostAsync($"/api/session/{second}/delete", content: null);

        // It is gone from the view, so removing it again is a client mistake rather than a no-op.
        var again = await _http.PostAsync($"/api/session/{second}/delete", content: null);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var unknown = await _http.PostAsync("/api/session/9999/retract", content: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_raw_log_keeps_removed_events_and_the_last_seq_does_not_rewind()
    {
        await SeedAsync("one");
        var second = await SeedAsync("two");

        var before = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        await _http.PostAsync($"/api/session/{second}/delete", content: null);
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/session");

        // Ids are never reused: the next append continues past the highest seq ever assigned.
        Assert.True(after.GetProperty("lastSeq").GetInt64() > before.GetProperty("lastSeq").GetInt64());

        var raw = new List<string>();
        await foreach (var e in _context.Session.ReadAllAsync())
        {
            raw.Add(e.Body);
        }

        Assert.Contains("two", raw);
        Assert.Contains("delete #" + second, raw);
    }
}
