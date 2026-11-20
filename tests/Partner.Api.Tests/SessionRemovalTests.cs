using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Sessions;
using Partner.Api.Agents;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Memory;
using Partner.Core.Queue;
using System.Text.RegularExpressions;

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
    public async Task History_tool_reads_compacted_sources_but_not_withdrawn_sources()
    {
        var visible = await SeedAsync("old E_ACCESSDENIED detail");
        var withdrawn = await SeedAsync("withdrawn E_ACCESSDENIED secret");
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Edit, Source = InputSource.Text, EditedFromSeq = visible,
            Body = "revised E_ACCESSDENIED detail",
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Summary, Source = InputSource.Text, Body = "summary",
            Meta = new Dictionary<string, string> { ["compactedRanges"] = $"[[{visible},{withdrawn}]]" },
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract, Source = InputSource.Text, Body = "withdrawn",
            Meta = new Dictionary<string, string> { ["fromSeq"] = withdrawn.ToString(), ["toSeq"] = withdrawn.ToString() },
        });
        var legacy = await SeedAsync("legacy E_ACCESSDENIED detail");
        var legacySummary = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Summary, Source = InputSource.Text, Body = "old summary",
            Meta = new Dictionary<string, string> { ["coveredFrom"] = legacy.ToString(), ["coveredTo"] = legacy.ToString() },
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract, Source = InputSource.Text,
            Body = $"compacted #{legacy}..{legacy} into #{legacySummary}",
            Meta = new Dictionary<string, string> { ["fromSeq"] = legacy.ToString(), ["toSeq"] = legacy.ToString() },
        });
        var disguised = await SeedAsync("hidden E_ACCESSDENIED detail");
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract, Source = InputSource.Text,
            Body = $"compacted #{disguised}..{disguised} into #{legacySummary}",
            Meta = new Dictionary<string, string> { ["fromSeq"] = disguised.ToString(), ["toSeq"] = disguised.ToString() },
        });

        var tool = new SessionHistoryTool(_context.Session);
        var search = await tool.InvokeAsync("{\"query\":\"e_accessdenied\"}");
        Assert.Contains($"Seq={visible}", search.Content);
        Assert.Contains($"Seq={legacy}", search.Content);
        Assert.DoesNotContain($"Seq={disguised}", search.Content);
        Assert.Contains("revised E_ACCESSDENIED", search.Content);
        Assert.DoesNotContain("withdrawn", search.Content);
        var read = await tool.InvokeAsync($"{{\"seq\":{visible}}}");
        Assert.Contains("revised E_ACCESSDENIED detail", read.Content);
        var hidden = await tool.InvokeAsync($"{{\"seq\":{withdrawn}}}");
        Assert.DoesNotContain("E_ACCESSDENIED", hidden.Content);
        var hiddenDisguised = await tool.InvokeAsync($"{{\"seq\":{disguised}}}");
        Assert.DoesNotContain("E_ACCESSDENIED", hiddenDisguised.Content);
    }

    private sealed class HistoryPagingLlm(long seq) : ILlmClient
    {
        public List<List<LlmMessage>> Requests { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Requests.Add(request.Messages.ToList());
            var step = Requests.Count;
            string? args = step switch
            {
                1 => JsonSerializer.Serialize(new { seq }),
                2 => JsonSerializer.Serialize(new { seq, offset = int.Parse(Regex.Match(
                    Requests[1].Single(m => m.Role == "tool" && m.ToolCallId == "history-1").Content!, @"nextOffset=(\d+)").Groups[1].Value) }),
                3 or 4 => "{\"query\":\"E_ACCESSDENIED\"}",
                _ => null,
            };
            return Task.FromResult(args is null
                ? new LlmResponse("done", [], "stop")
                : new LlmResponse(null, [new LlmToolCall($"history-{step}", "session_history", args)], "tool_calls"));
        }
    }

    [Fact]
    public async Task History_pages_fit_runner_budget_and_search_ignores_own_receipts()
    {
        var body = string.Concat(Enumerable.Range(0, 3000).Select(i => (char)('一' + i % 100)));
        var seq = await SeedAsync(body);
        var original = await SeedAsync("original E_ACCESSDENIED");
        var other = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.ToolResult, Source = InputSource.Text, Body = "other E_ACCESSDENIED",
            Meta = new Dictionary<string, string> { ["name"] = "other_tool" },
        });
        for (var i = 0; i < 9; i++)
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.ToolResult, Source = InputSource.Text, Body = "receipt E_ACCESSDENIED",
                Meta = new Dictionary<string, string> { ["name"] = "session_history" },
            });

        var llm = new HistoryPagingLlm(seq);
        var tools = new InMemoryToolRegistry();
        tools.Register(new SessionHistoryTool(_context.Session));
        var counter = new ApproximateTokenCounter();
        var options = new AgentTurnOptions { ContextWindowTokens = 4096, MaxTokens = 1024, MaxSteps = 8 };
        var runner = new AgentTurnRunner(_context.Session, llm, tools,
            new AllowListGovernance(["session_history"]), new DenyAllApprovalHandler(), new HookRegistry(),
            options: options, tokens: counter);
        await runner.RunAsync(new InputItem
        {
            ItemId = "history-test", Seq = 1, Status = InputItemStatus.Running, Source = InputSource.Text,
            Payload = "read history", IdempotencyKey = "text:history-test",
        });

        var first = llm.Requests[1].Single(m => m.Role == "tool" && m.ToolCallId == "history-1").Content!;
        var second = llm.Requests[2].Single(m => m.Role == "tool" && m.ToolCallId == "history-2").Content!;
        var firstEnd = int.Parse(Regex.Match(first, @"nextOffset=(\d+)").Groups[1].Value);
        var secondEnd = int.Parse(Regex.Match(second, @"nextOffset=(\d+)").Groups[1].Value);
        Assert.Equal(body[..firstEnd], first[(first.IndexOf('\n') + 1)..]);
        Assert.Equal(body[firstEnd..secondEnd], second[(second.IndexOf('\n') + 1)..]);
        var cap = ToolResultBudget.ResultCap(Math.Max(1,
            ContextBudget.PromptBudget(options.ContextWindowTokens, options.MaxTokens) - ToolResultBudget.SchemaTokens(tools.Specs, counter)));
        Assert.True(counter.Count(first) <= cap);
        Assert.True(counter.Count(second) <= cap);
        var search = llm.Requests[4].Single(m => m.Role == "tool" && m.ToolCallId == "history-4").Content!;
        Assert.Contains($"Seq={original}", search);
        Assert.Contains($"Seq={other}", search);
        Assert.DoesNotContain("receipt E_ACCESSDENIED", search);
        Assert.Equal(2, Regex.Matches(search, "E_ACCESSDENIED").Count);
    }

    [Fact]
    public async Task History_page_that_cannot_fit_does_not_advance()
    {
        var seq = await SeedAsync("some history");
        var result = await new SessionHistoryTool(_context.Session).InvokeAsync(
            $"{{\"seq\":{seq}}}", 1, new ApproximateTokenCounter());
        Assert.False(result.Ok);
        Assert.Contains("本页未返回", result.Content);
        Assert.DoesNotContain("nextOffset=", result.Content);
    }

    private sealed class HistoryClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [Fact]
    public async Task History_day_uses_original_event_time_visible_edits_and_exclusive_pages()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("history-test", TimeSpan.FromHours(2), "history-test", "history-test");
        var clock = new HistoryClock(new DateTimeOffset(2025, 4, 3, 23, 0, 0, TimeSpan.Zero), zone);
        var tool = new SessionHistoryTool(_context.Session, clock);
        async Task<long> Add(string body, DateTimeOffset at) => await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User, Source = InputSource.Text, Body = body, At = at,
        });
        var outside = await Add("needle previous day", new DateTimeOffset(2025, 4, 2, 21, 59, 59, TimeSpan.Zero));
        var edited = await Add("original", new DateTimeOffset(2025, 4, 2, 22, 0, 0, TimeSpan.Zero));
        var withdrawn = await Add("needle withdrawn", new DateTimeOffset(2025, 4, 3, 8, 0, 0, TimeSpan.Zero));
        var expected = new List<long> { edited };
        for (var i = 0; i < 9; i++)
            expected.Add(await Add($"needle {i}", new DateTimeOffset(2025, 4, 3, 9, i, 0, TimeSpan.Zero)));
        var nextDay = await Add("needle next day", new DateTimeOffset(2025, 4, 3, 22, 0, 0, TimeSpan.Zero));
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Edit, Source = InputSource.Text, EditedFromSeq = edited, Body = "needle revised",
            At = new DateTimeOffset(2025, 4, 4, 12, 0, 0, TimeSpan.Zero),
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Summary, Source = InputSource.Text, Body = "needle summary control",
            Meta = new Dictionary<string, string> { ["compactedRanges"] = $"[[{edited},{withdrawn}]]" },
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract, Source = InputSource.Text, Body = "withdrawn",
            Meta = new Dictionary<string, string> { ["fromSeq"] = withdrawn.ToString(), ["toSeq"] = withdrawn.ToString() },
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.ToolResult, Source = InputSource.Text, Body = "needle receipt",
            At = new DateTimeOffset(2025, 4, 3, 10, 0, 0, TimeSpan.Zero),
            Meta = new Dictionary<string, string> { ["name"] = "session_history" },
        });

        var counter = new ApproximateTokenCounter();
        var first = await tool.InvokeAsync("{\"day\":\"yesterday\",\"query\":\"needle\"}", 1000, counter);
        Assert.True(first.Ok);
        Assert.Contains("日期=2025-04-03", first.Content);
        Assert.DoesNotContain($"Seq={outside} ", first.Content);
        Assert.DoesNotContain($"Seq={nextDay} ", first.Content);
        Assert.DoesNotContain($"Seq={withdrawn} ", first.Content);
        Assert.DoesNotContain("needle receipt", first.Content);
        Assert.DoesNotContain("needle summary control", first.Content);
        var cursor = long.Parse(Regex.Match(first.Content, @"beforeSeq=(\d+)").Groups[1].Value);
        var second = await tool.InvokeAsync($"{{\"day\":\"2025-04-03\",\"query\":\"needle\",\"beforeSeq\":{cursor}}}", 1000, counter);
        var found = Regex.Matches(first.Content + "\n" + second.Content, @"(?:^|\n)Seq=(\d+)")
            .Select(m => long.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(expected.OrderDescending(), found.OrderDescending());
        var budget = counter.Count(first.Content) - 1;
        var limited = await tool.InvokeAsync("{\"day\":\"2025-04-03\",\"query\":\"needle\"}", budget, counter);
        Assert.True(limited.Ok);
        Assert.True(counter.Count(limited.Content) <= budget);
        var limitedEntries = Regex.Matches(limited.Content, @"(?:^|\n)Seq=(\d+)");
        var limitedCursor = long.Parse(Regex.Match(limited.Content, @"beforeSeq=(\d+)").Groups[1].Value);
        Assert.Equal(long.Parse(limitedEntries[limitedEntries.Count - 1].Groups[1].Value), limitedCursor);
        var continuation = await tool.InvokeAsync($"{{\"day\":\"2025-04-03\",\"query\":\"needle\",\"beforeSeq\":{limitedCursor}}}", 1000, counter);
        var combined = Regex.Matches(limited.Content + "\n" + continuation.Content, @"(?:^|\n)Seq=(\d+)")
            .Select(m => long.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(expected.OrderDescending(), combined.OrderDescending());
        Assert.Contains("needle revised", second.Content);
        var read = await tool.InvokeAsync($"{{\"seq\":{edited}}}");
        Assert.Contains("needle revised", read.Content);
        var tiny = await tool.InvokeAsync("{\"day\":\"2025-04-03\"}", 1, counter);
        Assert.False(tiny.Ok);
        Assert.DoesNotContain("beforeSeq=", tiny.Content);
        var empty = await tool.InvokeAsync("{\"day\":\"2025-04-01\"}");
        Assert.Contains("没有匹配结果", empty.Content);
        Assert.Contains("日期=2025-04-01", empty.Content);
    }

    [Fact]
    public async Task History_day_uses_local_midnights_across_daylight_saving()
    {
        var startRule = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var endRule = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(1), startRule, endRule);
        var zone = TimeZoneInfo.CreateCustomTimeZone("history-dst", TimeSpan.FromHours(-5), "history-dst", "standard", "daylight", [rule]);
        var inside = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User, Source = InputSource.Text, Body = "inside DST day",
            At = new DateTimeOffset(2025, 3, 10, 3, 59, 0, TimeSpan.Zero),
        });
        var outside = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User, Source = InputSource.Text, Body = "outside DST day",
            At = new DateTimeOffset(2025, 3, 10, 4, 0, 0, TimeSpan.Zero),
        });
        var tool = new SessionHistoryTool(_context.Session, new HistoryClock(DateTimeOffset.UtcNow, zone));
        var result = await tool.InvokeAsync("{\"day\":\"2025-03-09\"}");
        Assert.Contains($"Seq={inside} ", result.Content);
        Assert.DoesNotContain($"Seq={outside} ", result.Content);
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
