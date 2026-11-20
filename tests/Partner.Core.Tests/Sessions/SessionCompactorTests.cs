using Partner.Core.Agent;
using Partner.Core.Agent.Delegation;
using Partner.Core.Memory;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Sessions;

/// <summary>Compaction keeps original events in the log and replaces whole turns with one checkpoint.</summary>
public sealed class SessionCompactorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-compactor-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ILlmClient Summarizer(string text, string finishReason = "stop") => new ScriptedLlmClient(new[]
    {
        new LlmResponse(text, Array.Empty<LlmToolCall>(), finishReason),
    });

    private async Task<ISessionStore> SeedAsync(params string[] bodies)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);

        foreach (var body in bodies)
        {
            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = body + " " + new string('x', 120),
            });
        }

        return store;
    }

    [Fact]
    public async Task Nothing_happens_while_the_history_fits()
    {
        await using var store = await SeedAsync("one", "two");

        var result = await new SessionCompactor(store, Summarizer("summary")).CompactAsync(promptBudget: 500);

        Assert.False(result.Compacted);
        Assert.Contains("无需压缩", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, (await store.ProjectAsync()).Items.Count);
    }

    [Fact]
    public async Task Older_events_are_replaced_by_one_summary()
    {
        await using var store = await SeedAsync("one", "two", "three", "four", "five");
        await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.Assistant, Source = InputSource.Text, Body = "latest answer" });

        var result = await new SessionCompactor(store, Summarizer("短摘要")).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        Assert.True(result.Compacted);
        Assert.Equal(4, result.FoldedEvents);

        var view = await store.ProjectAsync();

        // The newest turn stays intact even when it is too large for the target.
        Assert.Equal(3, view.Items.Count);
        Assert.StartsWith("five ", view.Items[0].Body);
        Assert.Equal("latest answer", view.Items[1].Body);
        Assert.Equal(SessionEventKind.Summary, view.Items[2].Kind);
        Assert.Equal("短摘要", view.Items[2].Body);

        var events = new List<SessionEvent>();
        await foreach (var e in store.ReadAllAsync()) events.Add(e);
        Assert.DoesNotContain(events, e => e.Kind == SessionEventKind.Retract);
        Assert.Equal(view.Items.Select(item => item.Seq), SessionProjection.Build(events).Items.Select(item => item.Seq));
    }

    [Fact]
    public async Task The_summarized_range_stops_being_sent_but_stays_in_the_log()
    {
        await using var store = await SeedAsync("one", "two", "three");

        await new SessionCompactor(store, Summarizer("前两条摘要")).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        var visible = (await store.ProjectAsync()).Items.Select(i => i.Body).ToList();
        Assert.DoesNotContain(visible, body => body.StartsWith("one ", StringComparison.Ordinal));
        Assert.DoesNotContain(visible, body => body.StartsWith("two ", StringComparison.Ordinal));

        var raw = new List<string>();
        await foreach (var e in store.ReadAllAsync())
        {
            raw.Add(e.Body);
        }

        Assert.Contains(raw, body => body.StartsWith("one ", StringComparison.Ordinal));
        Assert.Contains(raw, body => body.StartsWith("two ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_multi_line_summary_keeps_its_structure_and_the_log_still_parses()
    {
        await using var store = await SeedAsync("one", "two", "three");

        await new SessionCompactor(store, Summarizer("- 第一行\n- 第二行\n\n**小结**")).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        var summary = (await store.ProjectAsync()).Items.Single(i => i.Kind == SessionEventKind.Summary);
        Assert.Equal("- 第一行\n- 第二行\n\n**小结**", summary.Body);

        // The invariant: the log is one record per line and still parses whole.
        var count = 0;
        await foreach (var _ in store.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(4, count);
    }

    [Theory]
    [InlineData("   ", "stop")]
    [InlineData("非空但截断", "length")]
    public async Task An_empty_or_truncated_summary_is_reported_rather_than_written(string text, string finishReason)
    {
        await using var store = await SeedAsync("one", "two", "three");

        var result = await new SessionCompactor(store, Summarizer(text, finishReason)).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        Assert.False(result.Compacted);
        Assert.Contains("摘要为空或被截断", result.Reason, StringComparison.Ordinal);
        Assert.Equal(3, (await store.ProjectAsync()).Items.Count);
    }

    [Fact]
    public async Task Compacting_twice_folds_the_previous_summary_instead_of_keeping_both()
    {
        await using var store = await SeedAsync("one", "two", "three", "four", "five");

        await new SessionCompactor(store, Summarizer("第一次摘要")).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        // The same store keeps appending: the conversation continues after a compaction.
        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "six",
        });

        var second = await new SessionCompactor(store, Summarizer("第二次摘要")).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        Assert.True(second.Compacted);

        var view = await store.ProjectAsync();
        var summaries = view.Items.Count(i => i.Kind == SessionEventKind.Summary);

        // Exactly one summary at a time: otherwise every pass would add context instead of removing it.
        Assert.Equal(1, summaries);
        Assert.Equal("第二次摘要", view.Items.Single(i => i.Kind == SessionEventKind.Summary).Body);
    }

    [Fact]
    public async Task After_compacting_for_the_history_budget_the_assembler_has_nothing_left_to_drop()
    {
        // A system block that eats a good part of the window: compacting against the *whole* budget would keep
        // more than the assembler sends, and the next turn would silently drop it again.
        await using var store = await SeedAsync(
            Enumerable.Range(0, 12).Select(i => $"message number {i} with some padding words to cost tokens").ToArray());
        var assembler = new Partner.Core.Agent.SessionWindowContextAssembler(
            store,
            contextTokens: 600,
            reserveForOutput: 60,
            baseSystemPrompt: string.Join(' ', Enumerable.Repeat("system instruction", 10)));

        var before = await assembler.MeasureAsync();
        Assert.True(before.FoldedItems > 0, "the fixture should overflow the window");

        var result = await new SessionCompactor(store, Summarizer("short summary"))
            .CompactAsync(SessionCompactor.HistoryBudget(before), summaryInputBudget: before.BudgetTokens);
        Assert.True(result.Compacted, result.Reason);

        var after = await assembler.MeasureAsync();
        Assert.True(after.Fraction <= 0.6, $"expected headroom after compaction, got {after.Fraction:P0}");
        Assert.Equal(0, after.FoldedItems);
    }

    [Theory]
    [InlineData(SessionEventKind.User, InputSource.Agent)]
    public async Task Savings_compare_the_rendered_source_frame_not_just_the_body(SessionEventKind kind, InputSource source)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using var ownedStore = store;
        await store.AppendAsync(new SessionEvent { Kind = kind, Source = source, Body = "x" });
        await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.User, Source = InputSource.Text, Body = "latest" });

        // The summary costs more than the raw body, but less than the source-framed message.
        var result = await new SessionCompactor(store, Summarizer(new string('x', 40)))
            .CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        Assert.True(result.Compacted, result.Reason);
        Assert.Contains((await store.ProjectAsync()).Items, item => item.Body == "latest");
    }

    [Fact]
    public async Task Mixed_compaction_never_summarizes_scheduled_turns()
    {
        await using var store = await SeedAsync("one", "two", "three");
        await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.User, Source = InputSource.Schedule, Body = "scheduled instruction" });
        await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.Assistant, Source = InputSource.Text, Body = "scheduled result" });
        await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.User, Source = InputSource.Text, Body = "latest" });
        var llm = new ScriptedLlmClient([new LlmResponse("short summary", Array.Empty<LlmToolCall>(), "stop")]);

        var result = await new SessionCompactor(store, llm).CompactAsync(promptBudget: 8, summaryInputBudget: 500);

        Assert.True(result.Compacted, result.Reason);
        var transcript = string.Join("\n", llm.Requests.Single().Messages.Select(message => message.Content));
        Assert.DoesNotContain("scheduled instruction", transcript);
        Assert.DoesNotContain("scheduled result", transcript);
        var view = await store.ProjectAsync();
        Assert.Contains(view.Items, item => item.Body == "scheduled instruction");
        Assert.Contains(view.Items, item => item.Body == "scheduled result");
    }

    [Fact]
    public async Task Scheduled_history_is_excluded_from_occupancy_and_new_summaries_but_remains_readable()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using var ownedStore = store;
        const string body = "tick";
        for (var i = 0; i < 6; i++)
        {
            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Schedule,
                Source = InputSource.Schedule,
                Body = body,
            });
            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Assistant,
                Source = InputSource.Text,
                Body = "done",
            });
        }

        var assembler = new SessionWindowContextAssembler(store, contextTokens: 1055, reserveForOutput: 100,
            baseSystemPrompt: "system instruction");
        var before = await assembler.MeasureAsync();
        Assert.Equal(955, before.BudgetTokens);
        Assert.Equal(0, before.KeptItems);
        Assert.Equal(0, before.FoldedItems);
        var original = (await store.ProjectAsync()).Items.ToArray();
        var llm = new ScriptedLlmClient(Array.Empty<LlmResponse>());

        var result = await new SessionCompactor(store, llm)
            .CompactAsync(SessionCompactor.HistoryBudget(before), summaryInputBudget: before.BudgetTokens);

        Assert.False(result.Compacted);
        Assert.Empty(llm.Requests);
        Assert.Equal(before.UsedTokens, (await assembler.MeasureAsync()).UsedTokens);
        var view = await store.ProjectAsync();
        Assert.Equal(original, view.Items);
        Assert.Equal(12, view.Items.Count);
    }
}
