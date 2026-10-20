using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// Removing history from the session view: delete the latest conversation for good, or withdraw from a point
/// and take it back until the next message is sent.
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

    /// <summary>One turn as the runner writes it: markers, the message, the reply.</summary>
    private async Task<(long Start, long User, long Reply)> SeedTurnAsync(
        string user, string reply, InputSource source = InputSource.Text)
    {
        var item = new Dictionary<string, string> { ["itemId"] = "item-" + Guid.NewGuid().ToString("n")[..6] };
        var start = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.TurnStart,
            Source = source,
            Body = "turn",
            Meta = item,
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.StepStart,
            Source = source,
            Body = "step 1",
            Meta = item,
        });
        var userSeq = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = source,
            Body = user,
            Meta = item,
        });
        var replySeq = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = source,
            Body = reply,
            Meta = item,
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.TurnEnd,
            Source = source,
            Body = "turn completed",
            Meta = new Dictionary<string, string>(item) { ["reason"] = "completed" },
        });

        return (start, userSeq, replySeq);
    }

    private async Task<List<string>> VisibleBodiesAsync()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        return view.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("body").GetString()!).ToList();
    }

    private async Task<JsonElement> ConversationAsync() =>
        (await _http.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("conversation");

    private async Task<long> FirstVisibleUserAsync()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        return view.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "User").GetProperty("seq").GetInt64();
    }

    [Fact]
    public async Task Only_the_latest_conversation_can_be_deleted()
    {
        var first = await SeedTurnAsync("第一问", "第一答");
        var second = await SeedTurnAsync("第二问", "第二答");

        // Not an earlier one: removing history from behind newer history leaves the rest answering things never asked.
        var earlier = await _http.PostAsync($"/api/session/{first.User}/delete", content: null);
        Assert.Equal(HttpStatusCode.Conflict, earlier.StatusCode);
        Assert.Contains("只能删除最新一次对话", await earlier.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(new[] { "第一问", "第一答", "第二问", "第二答" }, await VisibleBodiesAsync());

        // Whichever row of the latest conversation it is pressed on, the whole conversation goes.
        var latest = await _http.PostAsync($"/api/session/{second.Reply}/delete", content: null);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(new[] { "第一问", "第一答" }, await VisibleBodiesAsync());
    }

    [Fact]
    public async Task Deleting_the_latest_conversation_takes_its_text_and_its_markers_out_of_the_log()
    {
        await SeedTurnAsync("keep me", "kept reply");
        var doomed = await SeedTurnAsync("the secret that must not linger", "and its answer");

        var response = await _http.PostAsync($"/api/session/{doomed.User}/delete", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The point of the operation: the lines are gone from the file, not hidden behind a marker.
        var log = await LogTextAsync();
        Assert.DoesNotContain("the secret that must not linger", log, StringComparison.Ordinal);
        Assert.DoesNotContain("and its answer", log, StringComparison.Ordinal);
        Assert.Contains("keep me", log, StringComparison.Ordinal);

        // The turn's own markers went with it, and one content-free retraction stands for the removal so the
        // seq space stays monotonic.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(doomed.Start, body.GetProperty("removedFrom").GetInt64());
        Assert.Contains("purge", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revision_of_a_deleted_message_does_not_stay_behind_in_the_log()
    {
        var turn = await SeedTurnAsync("the original text", "reply");
        await _http.PostAsync($"/api/session/{turn.User}/edit", JsonContent.Create(new { body = "the revised text" }));

        await _http.PostAsync($"/api/session/{turn.User}/delete", content: null);

        var log = await LogTextAsync();
        Assert.DoesNotContain("the original text", log, StringComparison.Ordinal);
        Assert.DoesNotContain("the revised text", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_view_says_which_conversation_is_the_latest()
    {
        Assert.Equal(JsonValueKind.Null, (await ConversationAsync()).GetProperty("latestFromSeq").ValueKind);

        await SeedTurnAsync("一", "答一");
        var second = await SeedTurnAsync("二", "答二");

        var conversation = await ConversationAsync();
        Assert.Equal(second.User, conversation.GetProperty("latestFromSeq").GetInt64());
        Assert.Equal(JsonValueKind.Null, conversation.GetProperty("withdrawn").ValueKind);
    }

    [Fact]
    public async Task A_sub_agents_report_is_hidden_from_the_view_but_not_from_the_log()
    {
        await SeedTurnAsync("读一下 a.txt", "已交给文件阅读");
        var report = await SeedTurnAsync("[sub-agent report job-1 done]", "文件里写的是：你好", InputSource.Agent);

        var bodies = await VisibleBodiesAsync();

        Assert.DoesNotContain(bodies, body => body.StartsWith("[sub-agent", StringComparison.Ordinal));
        Assert.Contains("文件里写的是：你好", bodies);
        Assert.Contains("[sub-agent report", await LogTextAsync(), StringComparison.Ordinal);

        // It is not a conversation either: the latest one is still the person's.
        Assert.NotEqual(report.User, (await ConversationAsync()).GetProperty("latestFromSeq").GetInt64());
    }

    [Fact]
    public async Task Withdrawing_hides_the_conversation_and_everything_after_but_deletes_nothing()
    {
        await SeedTurnAsync("kept-q", "kept-a");
        var second = await SeedTurnAsync("withdrawn-q", "withdrawn-a");
        await SeedTurnAsync("later-q", "later-a");

        var response = await _http.PostAsync($"/api/session/{second.Reply}/withdraw", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(second.Start, body.GetProperty("withdrawnFrom").GetInt64());
        Assert.Equal(2, body.GetProperty("conversations").GetInt32());

        Assert.Equal(new[] { "kept-q", "kept-a" }, await VisibleBodiesAsync());
        Assert.Equal(2, (await ConversationAsync()).GetProperty("withdrawn").GetProperty("conversations").GetInt32());

        // Nothing was removed: that is what makes it something that can be taken back.
        var log = await LogTextAsync();
        Assert.Contains("withdrawn-q", log, StringComparison.Ordinal);
        Assert.Contains("later-a", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_withdrawal_can_be_cancelled_until_a_new_message_is_sent()
    {
        await SeedTurnAsync("kept-q", "kept-a");
        var second = await SeedTurnAsync("withdraw-me-q", "withdraw-me-a");
        await _http.PostAsync($"/api/session/{second.User}/withdraw", content: null);
        Assert.Equal(new[] { "kept-q", "kept-a" }, await VisibleBodiesAsync());

        var cancelled = await _http.PostAsync("/api/session/withdrawal/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(new[] { "kept-q", "kept-a", "withdraw-me-q", "withdraw-me-a" }, await VisibleBodiesAsync());
        Assert.Equal(JsonValueKind.Null, (await ConversationAsync()).GetProperty("withdrawn").ValueKind);

        // Nothing left to cancel.
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/session/withdrawal/cancel", content: null)).StatusCode);
    }

    [Fact]
    public async Task Sending_a_new_message_makes_the_withdrawal_final()
    {
        await SeedTurnAsync("kept-q", "kept-a");
        var second = await SeedTurnAsync("the withdrawn question", "the withdrawn answer");
        await _http.PostAsync($"/api/session/{second.User}/withdraw", content: null);
        Assert.Contains("the withdrawn question", await LogTextAsync(), StringComparison.Ordinal);

        var sent = await _http.PostAsJsonAsync("/api/input", new { source = "Text", payload = "a new sentence" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        // Final: the lines are gone from the file, the pending state is gone from the view, and it cannot be undone.
        var log = await LogTextAsync();
        Assert.DoesNotContain("the withdrawn question", log, StringComparison.Ordinal);
        Assert.DoesNotContain("the withdrawn answer", log, StringComparison.Ordinal);
        Assert.Contains("kept-q", log, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await ConversationAsync()).GetProperty("withdrawn").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/session/withdrawal/cancel", content: null)).StatusCode);
    }

    [Fact]
    public async Task Withdrawing_from_an_earlier_point_widens_the_pending_withdrawal_and_cancel_brings_back_all_of_it()
    {
        var first = await SeedTurnAsync("q1", "a1");
        var second = await SeedTurnAsync("q2", "a2");
        await SeedTurnAsync("q3", "a3");

        await _http.PostAsync($"/api/session/{second.User}/withdraw", content: null);
        Assert.Equal(new[] { "q1", "a1" }, await VisibleBodiesAsync());

        await _http.PostAsync($"/api/session/{first.User}/withdraw", content: null);
        Assert.Empty(await VisibleBodiesAsync());
        Assert.Equal(3, (await ConversationAsync()).GetProperty("withdrawn").GetProperty("conversations").GetInt32());

        await _http.PostAsync("/api/session/withdrawal/cancel", content: null);
        Assert.Equal(new[] { "q1", "a1", "q2", "a2", "q3", "a3" }, await VisibleBodiesAsync());
    }

    [Fact]
    public async Task Events_added_after_a_withdrawal_stay_visible()
    {
        await SeedTurnAsync("q1", "a1");
        var second = await SeedTurnAsync("q2", "a2");
        await _http.PostAsync($"/api/session/{second.User}/withdraw", content: null);

        // A sub-agent's reply arriving later is not part of what was withdrawn: the range was closed when it was made.
        await SeedTurnAsync("[sub-agent report]", "late answer", InputSource.Agent);

        Assert.Equal(new[] { "q1", "a1", "late answer" }, await VisibleBodiesAsync());
    }

    [Fact]
    public async Task Deleting_is_refused_while_a_withdrawal_is_waiting_to_be_confirmed_or_cancelled()
    {
        await SeedTurnAsync("q1", "a1");
        var second = await SeedTurnAsync("q2", "a2");
        await SeedTurnAsync("q3", "a3");
        await _http.PostAsync($"/api/session/{second.User}/withdraw", content: null);

        // The latest *visible* conversation is q1, but deleting it would silently make the withdrawal final.
        var response = await _http.PostAsync($"/api/session/{await FirstVisibleUserAsync()}/delete", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("撤回", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_seq_space_survives_a_deletion()
    {
        var turn = await SeedTurnAsync("one", "reply");

        await _http.PostAsync($"/api/session/{turn.User}/delete", content: null);

        // Seqs are never reused: memory records point back at them.
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(view.GetProperty("lastSeq").GetInt64() > turn.Reply);
    }

    [Fact]
    public async Task Removing_something_that_is_not_in_the_view_is_refused()
    {
        await SeedTurnAsync("q1", "a1");

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/session/9999/delete", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/session/9999/withdraw", content: null)).StatusCode);
    }
}
