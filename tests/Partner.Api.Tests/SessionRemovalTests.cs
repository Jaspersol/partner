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

    private async Task<string> LogTextAsync()
    {
        await using var stream = new FileStream(
            Path.Combine(_workspace, "session", "events.jsonl"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task An_edit_may_be_multiline()
    {
        // A model reply is markdown; editing one must not be a 500 because of its line breaks.
        var seq = await SeedAsync("single line");

        var response = await _http.PostAsync(
            $"/api/session/{seq}/edit",
            JsonContent.Create(new { body = "## 改过的标题\n\n- 一\n- 二" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        var item = view.GetProperty("items").EnumerateArray().First(i => i.GetProperty("seq").GetInt64() == seq);
        Assert.Equal("## 改过的标题\n\n- 一\n- 二", item.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Deleting_an_event_takes_its_text_out_of_the_log()
    {
        await SeedAsync("kept before");
        var second = await SeedAsync("the secret that must not linger");
        await SeedAsync("kept after");

        var response = await _http.PostAsync($"/api/session/{second}/delete", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The point of the whole operation: the line is gone from the file, not hidden behind a marker.
        var log = await LogTextAsync();
        Assert.DoesNotContain("the secret that must not linger", log, StringComparison.Ordinal);
        Assert.Contains("kept before", log, StringComparison.Ordinal);
        Assert.Contains("kept after", log, StringComparison.Ordinal);

        // One content-free retraction stands for the removal, so the seq space stays monotonic.
        Assert.Contains("purge", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retracting_takes_the_whole_tail_out_of_the_log()
    {
        await SeedAsync("kept");
        var second = await SeedAsync("first removed");
        await SeedAsync("second removed");

        await _http.PostAsync($"/api/session/{second}/retract", content: null);

        var log = await LogTextAsync();
        Assert.DoesNotContain("first removed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("second removed", log, StringComparison.Ordinal);
        Assert.Contains("kept", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revision_of_a_removed_event_does_not_stay_behind_in_the_log()
    {
        var seq = await SeedAsync("the original text");
        await _http.PostAsync(
            $"/api/session/{seq}/edit",
            JsonContent.Create(new { body = "the revised text" }));

        await _http.PostAsync($"/api/session/{seq}/delete", content: null);

        var log = await LogTextAsync();
        Assert.DoesNotContain("the original text", log, StringComparison.Ordinal);
        Assert.DoesNotContain("the revised text", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_seq_space_survives_a_purge()
    {
        var first = await SeedAsync("one");
        var second = await SeedAsync("two");

        await _http.PostAsync($"/api/session/{first}/retract", content: null);

        // Seqs are never reused: memory records point back at them.
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(view.GetProperty("lastSeq").GetInt64() > second);
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
}
