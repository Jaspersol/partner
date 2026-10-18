using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>The parts of withdrawing that do not need a running host: what survives a restart, and where a conversation starts.</summary>
public sealed class WithdrawalServiceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-withdrawal-tests", Guid.NewGuid().ToString("N"));
    private JsonlSessionStore _session = null!;
    private MemoryStore _memory = null!;

    public Task InitializeAsync()
    {
        (_session, _) = JsonlSessionStore.Open(_dir);
        _memory = new MemoryStore(Path.Combine(_dir, "memory.db"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _memory.DisposeAsync();
        await _session.DisposeAsync();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private async Task<long> UserAsync(string body, InputSource source = InputSource.Text) =>
        await _session.AppendAsync(new SessionEvent { Kind = SessionEventKind.User, Source = source, Body = body });

    [Fact]
    public async Task A_withdrawal_made_before_a_restart_can_still_be_cancelled()
    {
        await UserAsync("one");
        var second = await UserAsync("two");
        await new WithdrawalService(_session, _memory).WithdrawAsync(second);

        // A new service over the same log is a restarted process.
        var restarted = new WithdrawalService(_session, _memory);
        var pending = await restarted.PendingAsync();

        Assert.NotNull(pending);
        Assert.Equal(second, pending!.FromSeq);
        Assert.Equal(1, pending.Conversations);
        Assert.True(await restarted.CancelAsync());
        Assert.Equal(2, (await _session.ProjectAsync()).Items.Count);
    }

    [Fact]
    public async Task Committing_with_nothing_pending_does_nothing()
    {
        await UserAsync("one");

        Assert.Null(await new WithdrawalService(_session, _memory).CommitAsync());
        Assert.Single((await _session.ProjectAsync()).Items);
    }

    [Fact]
    public async Task A_conversation_starts_at_a_message_somebody_sent_and_a_sub_agents_report_never_starts_one()
    {
        var first = await UserAsync("typed");
        await UserAsync("[report]", InputSource.Agent);
        var third = await UserAsync("spoken", InputSource.Voice);
        var view = await _session.ProjectAsync();

        Assert.Equal(new[] { first, third }, WithdrawalService.Starts(view));
        Assert.Equal(third, WithdrawalService.LatestStart(view));

        // The report belongs to the conversation before it.
        Assert.Equal(first, WithdrawalService.StartOf(view, first + 1));
    }

    [Fact]
    public async Task The_turn_markers_in_front_of_a_message_belong_to_its_conversation()
    {
        await _session.AppendAsync(new SessionEvent { Kind = SessionEventKind.TurnStart, Source = InputSource.Text, Body = "turn" });
        await _session.AppendAsync(new SessionEvent { Kind = SessionEventKind.StepStart, Source = InputSource.Text, Body = "step 1" });
        var user = await UserAsync("hello");

        Assert.Equal(1, await new WithdrawalService(_session, _memory).StartOfTurnAsync(user));
    }
}
