using Partner.Core.Memory;
using Partner.Core.Agent;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

/// <summary>
/// The message sequence a turn starts with. Two things are load-bearing: the shape (providers reject a
/// `system` message anywhere but the front — llama.cpp raises "System message must be at the beginning") and
/// the budget (what overflows a model is tokens, not turns).
/// </summary>
public sealed class ContextAssemblerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-context-tests", Guid.NewGuid().ToString("n"));

    /// <summary>Kept so the fixture can close the sink: on Windows a leaked handle makes the cleanup throw.</summary>
    private JsonlSessionStore? _opened;

    public void Dispose()
    {
        _opened?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static InputItem Input(string payload) => new()
    {
        ItemId = "item-1",
        Seq = 1,
        Status = InputItemStatus.Running,
        Source = InputSource.Text,
        Payload = payload,
        IdempotencyKey = "text:1",
    };

    private async Task<ISessionStore> SessionAsync(params (SessionEventKind Kind, string Body)[] events)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        _opened = store;

        foreach (var (kind, body) in events)
        {
            await store.AppendAsync(new SessionEvent
            {
                Kind = kind,
                Source = InputSource.Text,
                Body = body,
            });
        }

        return store;
    }

    /// <summary>Counts, and refuses to be counted asynchronously: the measurement must not reach the network.</summary>
    private sealed class SyncOnlyCounter : ITokenCounter
    {
        public int Calls { get; private set; }

        public int Count(string text)
        {
            Calls++;
            return Math.Max(1, text.Length / 4);
        }

        public ValueTask<int> CountAsync(string text, CancellationToken ct = default) =>
            throw new InvalidOperationException("the measurement must not ask a model for a count");
    }

    [Fact]
    public async Task Measuring_occupancy_never_reaches_the_model()
    {
        // The session view reports occupancy on every load. If that path could do I/O, a slow or dead model
        // endpoint would hang the view — which is exactly what it did before this split.
        var store = await SessionAsync(
            (SessionEventKind.User, "turn on the lamp"),
            (SessionEventKind.Assistant, "done"));

        var counter = new SyncOnlyCounter();
        var usage = await new SessionWindowContextAssembler(store, contextTokens: 512, reserveForOutput: 64, tokens: counter)
            .MeasureAsync();

        Assert.True(counter.Calls > 0);
        Assert.True(usage.UsedTokens > 0);
        Assert.Equal(512 - 64, usage.BudgetTokens);
        Assert.Equal(2, usage.KeptItems);
    }

    [Fact]
    public async Task A_turn_may_still_ask_the_model_for_a_count()
    {
        // The other half of the split: the assembler that decides what to send keeps using the native counter.
        var store = await SessionAsync((SessionEventKind.User, "turn on the lamp"));
        var counter = new SyncOnlyCounter();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SessionWindowContextAssembler(store, contextTokens: 512, reserveForOutput: 64, tokens: counter)
                .AssembleInitialAsync(Input("next"), Array.Empty<string>()));
    }

    [Fact]
    public async Task Only_the_first_message_may_be_a_system_message()
    {
        // The shape a real turn leaves behind: the user turn, the hook injection, the tool call and result.
        await using var store = await SessionAsync(
            (SessionEventKind.User, "turn on the lamp"),
            (SessionEventKind.System, "Available skills: ..."),
            (SessionEventKind.Tool, "lamp.set(study, on)"),
            (SessionEventKind.Assistant, "done"));

        var messages = await new SessionWindowContextAssembler(store)
            .AssembleInitialAsync(Input("and the curtains"), new[] { "Be concise." });

        Assert.Equal("system", messages[0].Role);
        Assert.DoesNotContain(messages.Skip(1), m => m.Role == "system");

        // The turn structure survives as roles: user, assistant, and the current input last.
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(m => m.Role));
        Assert.Equal("and the curtains", messages[^1].Content);
    }

    [Fact]
    public async Task Non_conversational_entries_are_folded_into_the_system_block_with_their_seq()
    {
        await using var store = await SessionAsync(
            (SessionEventKind.User, "turn on the lamp"),
            (SessionEventKind.System, "Available skills: smoke"),
            (SessionEventKind.Tool, "lamp.set(study, on)"));

        var messages = await new SessionWindowContextAssembler(store)
            .AssembleInitialAsync(Input("next"), Array.Empty<string>());

        var system = messages[0].Content!;

        // Nothing is dropped: the entries are still visible to the model, just in the legal place.
        Assert.Contains("#2 [System] Available skills: smoke", system, StringComparison.Ordinal);
        Assert.Contains("#3 [Tool] lamp.set(study, on)", system, StringComparison.Ordinal);

        // The user turn stays a turn — only non-conversational entries move.
        Assert.DoesNotContain("turn on the lamp", system, StringComparison.Ordinal);
        Assert.Contains(messages, m => m.Role == "user" && m.Content == "turn on the lamp");
    }

    [Fact]
    public async Task Hook_injections_land_in_the_system_block_not_as_extra_messages()
    {
        await using var store = await SessionAsync((SessionEventKind.User, "hi"));

        var messages = await new SessionWindowContextAssembler(store)
            .AssembleInitialAsync(Input("again"), new[] { "Skill: do the thing.", "Memory: user likes tea." });

        Assert.Single(messages, m => m.Role == "system");
        var system = messages[0].Content!;
        Assert.Contains("Skill: do the thing.", system, StringComparison.Ordinal);
        Assert.Contains("Memory: user likes tea.", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_turn_is_still_just_system_then_the_input()
    {
        await using var store = await SessionAsync();

        var messages = await new SessionWindowContextAssembler(store)
            .AssembleInitialAsync(Input("hello"), Array.Empty<string>());

        Assert.Equal(new[] { "system", "user" }, messages.Select(m => m.Role));
    }

    [Fact]
    public async Task The_window_is_a_token_budget_not_an_item_count()
    {
        await using var store = await SessionAsync(
            (SessionEventKind.User, "one"),
            (SessionEventKind.Assistant, "two"),
            (SessionEventKind.User, "three"),
            (SessionEventKind.Assistant, "four"));

        // Budget: 40 − 8 reserve = 32, minus the system block (~14) and the input (~5) leaves ~13 — room for
        // two short turns, so the two oldest fall out even though the count is small.
        var messages = await new SessionWindowContextAssembler(store, contextTokens: 40, reserveForOutput: 8)
            .AssembleInitialAsync(Input("five"), Array.Empty<string>());

        var bodies = messages.Select(m => m.Content).ToList();
        Assert.DoesNotContain("one", bodies);
        Assert.DoesNotContain("two", bodies);
        Assert.Contains("three", bodies);
        Assert.Contains("four", bodies);
    }

    [Fact]
    public async Task One_large_entry_costs_more_than_several_small_ones()
    {
        await using var store = await SessionAsync(
            (SessionEventKind.User, "short"),
            (SessionEventKind.User, new string('x', 400)));

        // 400 characters is ~100 tokens: a 70-token window holds the short turn but not the long one, and the
        // suffix rule means the long one takes the short one with it.
        var messages = await new SessionWindowContextAssembler(store, contextTokens: 70, reserveForOutput: 8)
            .AssembleInitialAsync(Input("next"), Array.Empty<string>());

        var bodies = messages.Select(m => m.Content).ToList();
        Assert.DoesNotContain(bodies, body => body is not null && body.Contains(new string('x', 400), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_system_block_and_the_current_input_are_always_sent()
    {
        await using var store = await SessionAsync((SessionEventKind.User, new string('y', 4000)));

        // Far too small for the history — but dropping the instructions or the question would be worse than
        // sending an over-long prompt, and a turn that answers nothing is not a turn.
        var messages = await new SessionWindowContextAssembler(store, contextTokens: 32, reserveForOutput: 8)
            .AssembleInitialAsync(Input("the question"), new[] { "Be concise." });

        Assert.Equal("system", messages[0].Role);
        Assert.Contains("Be concise.", messages[0].Content!, StringComparison.Ordinal);
        Assert.Equal("the question", messages[^1].Content);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task A_summary_is_context_and_does_not_consume_the_turn_budget()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Summary,
                Source = InputSource.Text,
                Body = "earlier: the user asked about lamps",
            });
            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = "and now?",
            });

            var messages = await new SessionWindowContextAssembler(store, contextTokens: 64, reserveForOutput: 8)
                .AssembleInitialAsync(Input("next"), Array.Empty<string>());

            // The summary rides in the system block, so the turn budget is spent on turns.
            Assert.Contains("earlier: the user asked about lamps", messages[0].Content!, StringComparison.Ordinal);
            Assert.Contains(messages, m => m.Content == "and now?");
        }
    }
}
