using Partner.Core.Agent;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

/// <summary>
/// The message sequence a turn starts with. Providers are strict about its shape: a chat-completions
/// conversation is system content first, then the turns — llama.cpp's templates reject anything else with
/// "System message must be at the beginning", which is exactly what a session window containing hook
/// injections and tool results used to produce on the second turn and later.
/// </summary>
public sealed class ContextAssemblerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-context-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
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

    [Fact]
    public async Task Only_the_first_message_may_be_a_system_message()
    {
        // The shape a real turn leaves behind: the user turn, the hook injection, the tool call and result.
        await using var store = await SessionAsync(
            (SessionEventKind.User, "turn on the lamp"),
            (SessionEventKind.System, "Available skills: ..."),
            (SessionEventKind.Tool, "lamp.set(study, on)"),
            (SessionEventKind.Assistant, "done"));

        var messages = await new SessionWindowContextAssembler(store, windowItems: 20)
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

        var messages = await new SessionWindowContextAssembler(store, windowItems: 20)
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

        var messages = await new SessionWindowContextAssembler(store, windowItems: 20)
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

        var messages = await new SessionWindowContextAssembler(store, windowItems: 20)
            .AssembleInitialAsync(Input("hello"), Array.Empty<string>());

        Assert.Equal(new[] { "system", "user" }, messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Only_the_last_window_items_are_sent()
    {
        await using var store = await SessionAsync(
            (SessionEventKind.User, "one"),
            (SessionEventKind.Assistant, "two"),
            (SessionEventKind.User, "three"),
            (SessionEventKind.Assistant, "four"));

        var messages = await new SessionWindowContextAssembler(store, windowItems: 2)
            .AssembleInitialAsync(Input("five"), Array.Empty<string>());

        var bodies = messages.Select(m => m.Content).ToList();
        Assert.DoesNotContain("one", bodies);
        Assert.DoesNotContain("two", bodies);
        Assert.Contains("three", bodies);
        Assert.Contains("four", bodies);
    }
}
