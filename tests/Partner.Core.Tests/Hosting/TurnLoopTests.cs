using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Hosting;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Hosting;

/// <summary>Waits on a condition that only an in-flight turn can satisfy, with a bounded budget.</summary>
internal static class Eventually
{
    public static async Task<bool> IsTrueAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return condition();
    }
}

/// <summary>Completes only when the test releases it, so a turn can be held in flight deterministically.</summary>
internal sealed class GatedLlmClient : ILlmClient
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Started { get; private set; }

    public void Release() => _release.TrySetResult();

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Started = true;
        await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        return new LlmResponse("done", Array.Empty<LlmToolCall>(), "stop");
    }
}

internal sealed class ThrowingLlmClient(string message) : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException(message);
}

/// <summary>Acceptance tests for the single-consumer loop (design doc §2.2, §2.9).</summary>
public sealed class TurnLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-turnloop-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static AgentTurnRunner Runner(ISessionStore session, ILlmClient llm) =>
        new(
            session,
            llm,
            new InMemoryToolRegistry(),
            new AllowListGovernance(Array.Empty<string>()),
            new DenyAllApprovalHandler(),
            new HookRegistry());

    private static InputItem Input(string itemId, string payload, long seq) => new()
    {
        ItemId = itemId,
        Seq = seq,
        Source = InputSource.Text,
        Payload = payload,
        IdempotencyKey = $"text:{itemId}",
    };

    /// <summary>The latest status the log carries for an item.</summary>
    private static async Task<InputItemStatus?> StatusAsync(IInputQueue queue, string itemId)
    {
        InputItemStatus? status = null;
        await foreach (var entry in queue.ReadLogAsync())
        {
            if (entry.ItemId == itemId)
            {
                status = entry.Status;
            }
        }

        return status;
    }

    private static async Task<List<SessionEventKind>> KindsAsync(ISessionStore session)
    {
        var kinds = new List<SessionEventKind>();
        await foreach (var e in session.ReadAllAsync())
        {
            kinds.Add(e.Kind);
        }

        return kinds;
    }

    [Fact]
    public async Task A_queued_item_is_claimed_turned_and_settled_as_done()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        await queue.EnqueueAsync(Input("item-1", "hello", 1));

        var loop = new TurnLoop(queue, Runner(store, new ScriptedLlmClient(new[]
        {
            new LlmResponse("hi there", Array.Empty<LlmToolCall>(), "stop"),
        })), store);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);

        Assert.True(await Eventually.IsTrueAsync(() => loop.Stats.Completed == 1), "the turn never completed");
        stop.Cancel();
        await running;

        Assert.Equal(InputItemStatus.Done, await StatusAsync(queue, "item-1"));
        Assert.Equal(new TurnLoopStats(1, 0, 0, 0), loop.Stats);

        var kinds = await KindsAsync(store);
        Assert.Equal(SessionEventKind.TurnStart, kinds[0]);
        Assert.Equal(SessionEventKind.TurnEnd, kinds[^1]);
        Assert.Contains(SessionEventKind.User, kinds);
        Assert.Contains(SessionEventKind.Assistant, kinds);
    }

    [Fact]
    public async Task Items_are_turned_in_arrival_order()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        await queue.EnqueueAsync(Input("item-1", "first", 1));
        await queue.EnqueueAsync(Input("item-2", "second", 2));
        await queue.EnqueueAsync(Input("item-3", "third", 3));

        var loop = new TurnLoop(queue, Runner(store, new ScriptedLlmClient(new[]
        {
            new LlmResponse("a", Array.Empty<LlmToolCall>(), "stop"),
            new LlmResponse("b", Array.Empty<LlmToolCall>(), "stop"),
            new LlmResponse("c", Array.Empty<LlmToolCall>(), "stop"),
        })), store);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);

        Assert.True(await Eventually.IsTrueAsync(() => loop.Stats.Completed == 3), "not every item completed");
        stop.Cancel();
        await running;

        var spoken = new List<string>();
        await foreach (var e in store.ReadAllAsync())
        {
            if (e.Kind == SessionEventKind.User)
            {
                spoken.Add(e.Body);
            }
        }

        Assert.Equal(new[] { "first", "second", "third" }, spoken);
    }

    [Fact]
    public async Task A_failing_turn_settles_the_item_as_failed_and_the_loop_keeps_going()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        await queue.EnqueueAsync(Input("item-1", "boom", 1));

        var loop = new TurnLoop(queue, Runner(store, new ThrowingLlmClient("model exploded")), store);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);

        Assert.True(await Eventually.IsTrueAsync(() => loop.Stats.Failed == 1), "the failure was not recorded");
        stop.Cancel();
        await running;

        Assert.Equal(InputItemStatus.Failed, await StatusAsync(queue, "item-1"));
        Assert.Equal(new TurnLoopStats(0, 1, 0, 0), loop.Stats);
    }

    [Fact]
    public async Task Cancelling_a_running_item_settles_it_as_canceled()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        await queue.EnqueueAsync(Input("item-1", "slow", 1));

        var llm = new GatedLlmClient();
        var loop = new TurnLoop(queue, Runner(store, llm), store);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);

        Assert.True(await Eventually.IsTrueAsync(() => llm.Started), "the turn never started");
        Assert.True(await queue.CancelAsync("item-1"), "cancel was refused");

        Assert.True(await Eventually.IsTrueAsync(() => loop.Stats.Canceled == 1), "the cancel was not settled");
        stop.Cancel();
        await running;

        Assert.Equal(InputItemStatus.Canceled, await StatusAsync(queue, "item-1"));
    }

    [Fact]
    public async Task Stopping_the_loop_lets_the_turn_in_flight_finish()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        await queue.EnqueueAsync(Input("item-1", "slow", 1));

        var llm = new GatedLlmClient();
        var loop = new TurnLoop(queue, Runner(store, llm), store);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);

        Assert.True(await Eventually.IsTrueAsync(() => llm.Started), "the turn never started");

        // Shutdown must not cut a half-run turn in half: an interrupted item would only be re-queued later.
        stop.Cancel();
        Assert.False(running.IsCompleted, "the loop abandoned the turn in flight");

        llm.Release();
        await running;

        Assert.Equal(InputItemStatus.Done, await StatusAsync(queue, "item-1"));
        Assert.Equal(new TurnLoopStats(1, 0, 0, 0), loop.Stats);
    }

    [Fact]
    public async Task A_redelivered_item_whose_turn_already_ended_is_not_run_twice()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        await queue.EnqueueAsync(Input("item-1", "hello", 1));
        var claimed = await queue.TakeAsync();

        var loop = new TurnLoop(queue, Runner(store, new ScriptedLlmClient(new[]
        {
            new LlmResponse("first pass", Array.Empty<LlmToolCall>(), "stop"),
        })), store);

        await loop.ProcessAsync(claimed!);
        Assert.Equal(new TurnLoopStats(1, 0, 0, 0), loop.Stats);

        // The same item handed out again — what a crash mid-turn produces on the next start.
        var redelivered = new InputItem
        {
            ItemId = "item-1",
            Seq = 1,
            Status = InputItemStatus.Running,
            Source = InputSource.Text,
            Payload = "hello",
            IdempotencyKey = "text:item-1",
        };

        await loop.ProcessAsync(redelivered);

        Assert.Equal(new TurnLoopStats(1, 0, 0, 1), loop.Stats);
        Assert.Equal(InputItemStatus.Done, await StatusAsync(queue, "item-1"));

        var userEvents = (await KindsAsync(store)).Count(kind => kind == SessionEventKind.User);
        Assert.Equal(1, userEvents);
    }

    [Fact]
    public async Task An_empty_queue_returns_as_soon_as_the_token_is_cancelled()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        await using var store = session;
        await using var queue = new JsonlInputQueue(_dir);

        var loop = new TurnLoop(queue, Runner(store, new ScriptedLlmClient(Array.Empty<LlmResponse>())), store);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);

        await Task.Delay(20);
        stop.Cancel();

        await running;
        Assert.Equal(TurnLoopStats.Empty, loop.Stats);
    }
}
