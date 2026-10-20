using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Sessions;

/// <summary>
/// Context compression. The raw log keeps everything; what changes is what the next turn is given: the older
/// events are replaced by one summary, and the summarized range is retracted so it stops being sent.
/// </summary>
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

    private static ILlmClient Summarizer(string text) => new ScriptedLlmClient(new[]
    {
        new LlmResponse(text, Array.Empty<LlmToolCall>(), "stop"),
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
                Body = body,
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

        var result = await new SessionCompactor(store, Summarizer("前四条：讨论了开灯与窗帘。")).CompactAsync(promptBudget: 8);

        Assert.True(result.Compacted);
        Assert.Equal(4, result.FoldedEvents);

        var view = await store.ProjectAsync();

        // The newest turn the window kept, plus the summary standing in for the rest.
        Assert.Equal(2, view.Items.Count);
        Assert.Equal("five", view.Items[0].Body);
        Assert.Equal(SessionEventKind.Summary, view.Items[1].Kind);
        Assert.Equal("前四条：讨论了开灯与窗帘。", view.Items[1].Body);
    }

    [Fact]
    public async Task The_summarized_range_stops_being_sent_but_stays_in_the_log()
    {
        await using var store = await SeedAsync("one", "two", "three");

        await new SessionCompactor(store, Summarizer("前两条：摘要")).CompactAsync(promptBudget: 8);

        var visible = (await store.ProjectAsync()).Items.Select(i => i.Body).ToList();
        Assert.DoesNotContain("one", visible);
        Assert.DoesNotContain("two", visible);

        var raw = new List<string>();
        await foreach (var e in store.ReadAllAsync())
        {
            raw.Add(e.Body);
        }

        Assert.Contains("one", raw);
        Assert.Contains("two", raw);
    }

    [Fact]
    public async Task A_multi_line_summary_keeps_its_structure_and_the_log_still_parses()
    {
        await using var store = await SeedAsync("one", "two", "three");

        await new SessionCompactor(store, Summarizer("- 第一行\n- 第二行\n\n**小结**")).CompactAsync(promptBudget: 8);

        var summary = (await store.ProjectAsync()).Items.Single(i => i.Kind == SessionEventKind.Summary);
        Assert.Equal("- 第一行\n- 第二行\n\n**小结**", summary.Body);

        // The invariant: the log is one record per line and still parses whole.
        var count = 0;
        await foreach (var _ in store.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task An_empty_summary_is_reported_rather_than_written()
    {
        await using var store = await SeedAsync("one", "two", "three");

        var result = await new SessionCompactor(store, Summarizer("   ")).CompactAsync(promptBudget: 8);

        Assert.False(result.Compacted);
        Assert.Contains("没有返回摘要", result.Reason, StringComparison.Ordinal);
        Assert.Equal(3, (await store.ProjectAsync()).Items.Count);
    }

    [Fact]
    public async Task Compacting_twice_folds_the_previous_summary_instead_of_keeping_both()
    {
        await using var store = await SeedAsync("one", "two", "three", "four", "five");

        await new SessionCompactor(store, Summarizer("第一次摘要")).CompactAsync(promptBudget: 8);

        // The same store keeps appending: the conversation continues after a compaction.
        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "six",
        });

        var second = await new SessionCompactor(store, Summarizer("第二次摘要")).CompactAsync(promptBudget: 8);

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
            contextTokens: 240,
            reserveForOutput: 20,
            baseSystemPrompt: string.Join(' ', Enumerable.Repeat("system instruction", 30)));

        var before = await assembler.MeasureAsync();
        Assert.True(before.FoldedItems > 0, "the fixture should overflow the window");

        var result = await new SessionCompactor(store, Summarizer("short summary"))
            .CompactAsync(SessionCompactor.HistoryBudget(before));
        Assert.True(result.Compacted, result.Reason);

        var after = await assembler.MeasureAsync();
        Assert.Equal(0, after.FoldedItems);
    }
}
