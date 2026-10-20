using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Api;

/// <summary>A withdrawal that has been made and not yet made final.</summary>
/// <param name="RetractSeq">The seq of the retraction event that hides the range; cancelling removes exactly this line.</param>
/// <param name="FromSeq">First seq hidden (the start of the withdrawn conversation, control events included).</param>
/// <param name="ToSeq">Last seq hidden. Pinned when the withdrawal was made, so what arrives later stays visible.</param>
/// <param name="Conversations">How many conversations the withdrawal took.</param>
/// <param name="Events">How many visible events went with them.</param>
public sealed record PendingWithdrawal(long RetractSeq, long FromSeq, long ToSeq, int Conversations, int Events);

/// <summary>
/// Conversations, and the two ways to take them out of the session: delete the latest one for good, or withdraw
/// from a point and take it back until the next message is sent.
/// <para>
/// A <b>conversation</b> starts at a message somebody sent (typed, spoken or scheduled) and runs to the next such
/// message. A sub-agent's report is not one — it arrives inside whatever conversation caused it and is hidden
/// from the view.
/// </para>
/// <para>
/// <b>Withdrawing</b> hides the range behind an ordinary retraction event and leaves every byte in the log, which
/// is what makes it reversible; memories distilled from the withdrawn events are left alone until the withdrawal
/// becomes final. It becomes final — lines purged and their memories cascaded away — on <see cref="CommitAsync"/>.
/// At most one withdrawal is pending: withdrawing from an earlier point widens it, and cancelling brings back
/// everything that was withdrawn.
/// </para>
/// </summary>
public sealed class WithdrawalService
{
    private const string WithdrawnKey = "withdrawn";

    private readonly ISessionStore _session;
    private readonly IMemoryStore _memory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PendingWithdrawal? _pending;
    private bool _loaded;

    public WithdrawalService(ISessionStore session, IMemoryStore memory)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    /// <summary>The seqs of the visible conversations' first messages, oldest first. Agent reports do not start one.</summary>
    public static IReadOnlyList<long> Starts(SessionView view) =>
        view.Items.Where(IsStart).Select(item => item.Seq).ToList();

    /// <summary>The first message of the latest visible conversation, or null when there is none.</summary>
    public static long? LatestStart(SessionView view) => Starts(view) is { Count: > 0 } starts ? starts[^1] : null;

    /// <summary>The first message of the conversation <paramref name="seq"/> belongs to; <paramref name="seq"/> itself when it precedes them all.</summary>
    public static long StartOf(SessionView view, long seq)
    {
        var found = 0L;
        foreach (var start in Starts(view))
        {
            if (start <= seq)
            {
                found = start;
            }
        }

        return found > 0 ? found : seq;
    }

    private static bool IsStart(SessionViewItem item) => item.Kind == SessionEventKind.User && item.Source != InputSource.Agent;

    public async Task<PendingWithdrawal?> PendingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return _pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The first seq of the turn that holds <paramref name="messageSeq"/>: the turn and step markers written just
    /// before the message belong to it, and leaving them behind would orphan control events no view shows.
    /// </summary>
    public async Task<long> StartOfTurnAsync(long messageSeq, CancellationToken ct = default)
    {
        long? runStart = null;
        await foreach (var logged in _session.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (logged.Seq == messageSeq)
            {
                return runStart ?? messageSeq;
            }

            runStart = logged.Kind is SessionEventKind.TurnStart or SessionEventKind.StepStart
                ? runStart ?? logged.Seq
                : null;
        }

        return messageSeq;
    }

    /// <summary>
    /// Hides <paramref name="seq"/>'s conversation and everything after it. Null when <paramref name="seq"/> is not in the view.
    /// </summary>
    public async Task<PendingWithdrawal?> WithdrawAsync(long seq, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            var view = await _session.ProjectAsync(ct).ConfigureAwait(false);
            if (view.Items.All(item => item.Seq != seq))
            {
                return null;
            }

            var start = StartOf(view, seq);
            var from = await StartOfTurnAsync(start, ct).ConfigureAwait(false);
            var to = _session.LastSeq;

            // What an earlier pending withdrawal hid is part of this one now, but it is no longer in the view to be counted.
            var conversations = Starts(view).Count(candidate => candidate >= start) + (_pending?.Conversations ?? 0);
            var events = view.Items.Count(item => item.Seq >= start) + (_pending?.Events ?? 0);

            var retractSeq = await _session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Retract,
                Source = InputSource.Text,
                Body = $"withdraw #{from}..#{to}",
                Meta = new Dictionary<string, string>(SessionRetraction.Between(from, to)) { [WithdrawnKey] = "true" },
            }, ct).ConfigureAwait(false);

            // The earlier withdrawal lies wholly inside this one, so it is redundant now. Removed after the new one
            // is in place, never before, so nothing flickers back into view.
            if (_pending is { } previous)
            {
                await _session.PurgeAsync(previous.RetractSeq, previous.RetractSeq, ct).ConfigureAwait(false);
            }

            _pending = new PendingWithdrawal(retractSeq, from, to, conversations, events);
            return _pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Brings back everything the pending withdrawal hid. False when there is none.</summary>
    public async Task<bool> CancelAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_pending is not { } pending)
            {
                return false;
            }

            // The hidden lines were never touched; taking the retraction out of the log is the whole undo.
            await _session.PurgeAsync(pending.RetractSeq, pending.RetractSeq, ct).ConfigureAwait(false);
            _pending = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes the pending withdrawal final: the hidden lines leave the log and what was remembered from them leaves
    /// the memory. Called before a new message is queued, so that nothing from the withdrawn conversations can be
    /// recalled into the turn that message starts.
    /// </summary>
    /// <returns>The withdrawal that was made final, or null when there was none.</returns>
    public async Task<PendingWithdrawal?> CommitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_pending is not { } pending)
            {
                return null;
            }

            await _session.PurgeAsync(pending.FromSeq, pending.ToSeq, ct).ConfigureAwait(false);
            await MemoryDeletion.CascadeAsync(_memory, pending.FromSeq, pending.ToSeq, Actor.Human, ct).ConfigureAwait(false);
            _pending = null;
            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the pending withdrawal back from the log once, so one made before a restart is still there to cancel.</summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }

        SessionEvent? latest = null;
        await foreach (var logged in _session.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (logged.Kind == SessionEventKind.Retract
                && logged.Meta is not null
                && logged.Meta.GetValueOrDefault(WithdrawnKey) == "true")
            {
                latest = logged;
            }
        }

        if (latest is not null && SessionRetraction.TryRead(latest, out var from, out var to))
        {
            _pending = new PendingWithdrawal(
                latest.Seq,
                from,
                to,
                Conversations: await CountStartsAsync(from, to, ct).ConfigureAwait(false),
                Events: 0);
        }

        _loaded = true;
    }

    private async Task<int> CountStartsAsync(long from, long to, CancellationToken ct)
    {
        var count = 0;
        await foreach (var logged in _session.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (logged.Seq >= from && logged.Seq <= to && logged.Kind == SessionEventKind.User && logged.Source != InputSource.Agent)
            {
                count++;
            }
        }

        return count;
    }
}
