using System.Security.Cryptography;
using System.Text;
using Partner.Core.Memory;
using System.Text.Json;

namespace Partner.Api.Services;

/// <summary>One interaction: a user message and everything the service answered while it stayed on the prompt.</summary>
internal sealed class ServiceRound
{
    public long Seq { get; set; }
    public string Key { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public List<string> Assistants { get; set; } = new();
    public List<string> Tools { get; set; } = new();
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// True while a flush snapshot that included this round is in progress. A reply to a frozen round starts a
    /// new round instead of joining the one being summarized, whose text would otherwise be dropped at commit.
    /// <para>
    /// Not persisted per round: after a restart it is re-derived from the flush intent in the state file, so a
    /// stale flag from an abandoned snapshot cannot keep a round frozen forever.
    /// </para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Frozen { get; set; }

    /// <summary>
    /// Set when this round continues the conversation of a key whose earlier round was already summarized. The
    /// summarizer then describes only the follow-up, so the client's request is not repeated in the next window.
    /// </summary>
    public string? ContinuesKey { get; set; }
}

/// <summary>
/// A flush that has decided its record id and snapshot but has not committed yet. Written to the state file
/// <b>before</b> the memory record, so a crash between the write and the commit is recognized on the next flush
/// as "this window is already written" instead of as a record-id collision.
/// </summary>
internal sealed record FlushIntent(string RecordId, long SnapshotSeq);

/// <summary>A consistent read of the buffer for the summarizer and the state endpoint.</summary>
internal sealed record RoundsView(
    long SnapshotSeq,
    IReadOnlyList<ServiceRound> Rounds,
    int PendingCount,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? OldestAt,
    DateTimeOffset? LastFlushAt,
    string? LastMemoryId,
    int WindowSeq,
    FlushIntent? Flushing);

/// <summary>
/// The pending-interaction buffer for one service: <c>pending.jsonl</c> plus <c>state.json</c> under
/// <c>workspace/services/&lt;id&gt;/</c>. One <see cref="SemaphoreSlim"/> makes an append and a flush snapshot
/// mutually exclusive, so a window is never half-summarized.
/// <para>
/// The buffer file is an <b>append log</b>, not a snapshot: every append costs one line, so the per-request work
/// does not grow with the window size. It is compacted (rewritten from the in-memory list) at commit time and
/// whenever it outgrows the live set.
/// </para>
/// </summary>
internal sealed class PendingRounds
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>How many summarized keys are remembered so a cross-window continuation can be recognized.</summary>
    private const int SummarizedKeyMemory = 256;

    /// <summary>Append-log lines tolerated before it is rewritten without the compacted rounds.</summary>
    private const int LogSlack = 512;

    private readonly string _dir;
    private readonly string _roundsPath;
    private readonly string _statePath;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task<int>> _recoverWindowSeq;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly List<ServiceRound> _rounds = new();

    /// <summary>Recently summarized keys, oldest first. Bounded: only near-term tool-loop resends matter.</summary>
    private readonly List<string> _summarizedKeys = new();

    private long _nextSeq;
    private long _lastFlushedSeq;
    private int _windowSeq;
    private string? _lastMemoryId;
    private DateTimeOffset? _lastFlushAt;
    private DateTimeOffset? _lastActivityAt;
    private FlushIntent? _flushing;

    /// <summary>
    /// Set when the state file was missing or unreadable. The window number is then recovered from the memory
    /// store on the first snapshot, asynchronously, rather than by blocking in the constructor.
    /// </summary>
    private bool _windowSeqUnknown;

    /// <summary>Lines written since the file was last compacted, so the log is rewritten before it grows forever.</summary>
    private int _logLines;
    private DateTimeOffset _stateWrittenAt = DateTimeOffset.MinValue;

    /// <param name="recoverWindowSeq">
    /// Supplies the window number when the state file is missing or unreadable. Without it a crash mid-write would
    /// reset the counter to zero and the next summary would reuse the record id of an existing memory — which the
    /// summarizer would then mistake for a collision or for "already written".
    /// </param>
    public PendingRounds(string dir, TimeProvider clock, Func<CancellationToken, Task<int>>? recoverWindowSeq = null)
    {
        _dir = dir;
        _clock = clock;
        _recoverWindowSeq = recoverWindowSeq ?? (_ => Task.FromResult(0));
        _roundsPath = Path.Combine(dir, "pending.jsonl");
        _statePath = Path.Combine(dir, "state.json");
        Load();
    }

    private sealed class StateDto
    {
        public long NextSeq { get; set; }
        public long LastFlushedSeq { get; set; }
        public int WindowSeq { get; set; }
        public string? LastMemoryId { get; set; }
        public DateTimeOffset? LastFlushAt { get; set; }
        public DateTimeOffset? LastActivityAt { get; set; }
        public string? FlushingRecordId { get; set; }
        public long? FlushingSnapshotSeq { get; set; }
    }

    private sealed class LogLine
    {
        public string Op { get; set; } = "upsert";

        public ServiceRound? Round { get; set; }
    }

    private void Load()
    {
        Directory.CreateDirectory(_dir);

        var stateLoaded = false;
        if (File.Exists(_statePath))
        {
            try
            {
                var state = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(_statePath), Json);
                if (state is not null)
                {
                    _nextSeq = state.NextSeq;
                    _lastFlushedSeq = state.LastFlushedSeq;
                    _windowSeq = state.WindowSeq;
                    _lastMemoryId = state.LastMemoryId;
                    _lastFlushAt = state.LastFlushAt;
                    _lastActivityAt = state.LastActivityAt;
                    _flushing = state is { FlushingRecordId: { } pendingId, FlushingSnapshotSeq: { } pendingSeq }
                        ? new FlushIntent(pendingId, pendingSeq)
                        : null;
                    stateLoaded = true;
                }
            }
            catch (JsonException)
            {
                // A corrupt buffer starts from the memory store rather than taking the service down.
            }
        }

        // The counter is derived from what the service actually wrote, so a replayed window cannot collide with a
        // record that already exists. Deferred to the first snapshot, which is asynchronous.
        _windowSeqUnknown = !stateLoaded;

        ReplayLog();

        if (_rounds.Count > 0)
        {
            _nextSeq = Math.Max(_nextSeq, _rounds.Max(r => r.Seq) + 1);
        }

        // An interrupted flush still owns its snapshot: re-freeze it so replies arriving before the recovery do
        // not join rounds that are about to be committed.
        if (_flushing is { } intent)
        {
            foreach (var round in _rounds.Where(r => r.Seq <= intent.SnapshotSeq))
            {
                round.Frozen = true;
            }
        }
    }

    /// <summary>
    /// Replays the append log: later lines win, matched by <c>Seq</c>. A damaged line is skipped — a partial
    /// buffer is better than a dead service — and the file is compacted on the next commit.
    /// </summary>
    private void ReplayLog()
    {
        if (!File.Exists(_roundsPath))
        {
            return;
        }

        foreach (var line in File.ReadAllLines(_roundsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _logLines++;
            try
            {
                if (JsonSerializer.Deserialize<LogLine>(line, Json) is not { } entry)
                {
                    continue;
                }

                if (entry.Op == "clear")
                {
                    _rounds.Clear();
                    continue;
                }

                if (entry.Round is not { } round)
                {
                    continue;
                }

                var index = _rounds.FindIndex(r => r.Seq == round.Seq);
                if (index >= 0)
                {
                    _rounds[index] = round;
                }
                else
                {
                    _rounds.Add(round);
                    if (round.At > (_lastActivityAt ?? DateTimeOffset.MinValue))
                    {
                        _lastActivityAt = round.At;
                    }
                }
            }
            catch (JsonException)
            {
                // Skip the damaged line.
            }
        }

        _rounds.Sort((left, right) => left.Seq.CompareTo(right.Seq));
    }

    /// <summary>
    /// Records one assistant reply. The key covers the whole conversation prefix plus the last text user message,
    /// so a stateless client resending the same history during a tool loop accumulates into one round, while two
    /// different conversations that happen to share a greeting stay separate.
    /// </summary>
    public async Task AppendAsync(
        string userText,
        string prefixHash,
        string assistantText,
        IReadOnlyList<string> tools,
        CancellationToken ct)
    {
        var key = RoundKey(prefixHash, userText);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            var round = _rounds.FirstOrDefault(r => r.Key == key && !r.Frozen);
            if (round is null)
            {
                // A reply that arrives after its round was summarized — or while a flush holds that round frozen —
                // opens a new round that says it is a continuation, so the summary describes the follow-up rather
                // than repeating the request.
                var continues = _summarizedKeys.Contains(key) || _rounds.Any(r => r.Key == key && r.Frozen);
                round = new ServiceRound
                {
                    Seq = _nextSeq++,
                    Key = key,
                    User = userText,
                    At = now,
                    ContinuesKey = continues ? key : null,
                };
                _rounds.Add(round);
            }

            if (!string.IsNullOrEmpty(assistantText))
            {
                round.Assistants.Add(assistantText);
            }

            foreach (var tool in tools)
            {
                if (!string.IsNullOrEmpty(tool) && !round.Tools.Contains(tool))
                {
                    round.Tools.Add(tool);
                }
            }

            _lastActivityAt = now;
            await AppendLogAsync(round, ct).ConfigureAwait(false);
            await PersistStateAsync(force: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The next window number, derived from the summaries a service already wrote. Only needed when the state file
    /// is missing or unreadable: reusing a window number would reuse a record id.
    /// </summary>
    public static async Task<int> RecoverWindowSeqAsync(IMemoryStore memory, string serviceId, CancellationToken ct)
    {
        var records = await memory.ListAsync(
            new MemoryQuery(
                Origin: MemoryOrigins.Service(serviceId),
                IncludeInvalidated: true,
                Limit: int.MaxValue),
            ct).ConfigureAwait(false);

        var prefix = $"m-service-{serviceId}-";
        var highest = -1;
        foreach (var record in records)
        {
            if (!record.RecordId.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (int.TryParse(
                record.RecordId[prefix.Length..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var window))
            {
                highest = Math.Max(highest, window);
            }
        }

        return highest + 1;
    }

    /// <summary>Names the round a reply belongs to: the conversation prefix plus the user text, hashed.</summary>
    private static string RoundKey(string prefixHash, string userText) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{prefixHash}\u001f{userText}")));

    /// <summary>A read-only view for the state endpoint and the due check. Changes nothing.</summary>
    public async Task<RoundsView> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return View();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The window a flush is about to summarize. Freezes it under the same lock the view is taken under, so an
    /// append that lands before the commit goes to a new round instead of the one being summarized. Only a flush
    /// that has decided to write calls this; <see cref="ReleaseAsync"/> undoes it when the flush gives up.
    /// </summary>
    public async Task<RoundsView> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_windowSeqUnknown)
            {
                _windowSeq = Math.Max(_windowSeq, await _recoverWindowSeq(ct).ConfigureAwait(false));
                _windowSeqUnknown = false;
                await PersistStateAsync(force: true, ct).ConfigureAwait(false);
            }

            var view = View();
            foreach (var round in _rounds.Where(r => r.Seq <= view.SnapshotSeq))
            {
                round.Frozen = true;
            }

            return view;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records the flush intent before the memory record is written. A crash after the write then leaves both
    /// the record and the intent, and the next flush commits instead of writing a second record.
    /// </summary>
    public async Task BeginFlushAsync(string recordId, long snapshotSeq, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _flushing = new FlushIntent(recordId, snapshotSeq);
            await PersistStateAsync(force: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives the window back to the buffer: a flush that did not write (collision twice, model and template both
    /// failed before the write) unfreezes its snapshot and drops its intent, so later replies join their rounds again.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var round in _rounds)
            {
                round.Frozen = false;

                // A continuation of a round that was never summarized after all is just a round.
                if (round.ContinuesKey is { } continued && !_summarizedKeys.Contains(continued))
                {
                    round.ContinuesKey = null;
                }
            }

            await RewriteLogAsync(ct).ConfigureAwait(false);

            if (_flushing is not null)
            {
                _flushing = null;
                await PersistStateAsync(force: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private RoundsView View()
    {
        var snapshotSeq = _nextSeq - 1;
        var copy = _rounds
            .Where(r => r.Seq <= snapshotSeq)
            .Select(Clone)
            .ToList();

        return new RoundsView(
            snapshotSeq,
            copy,
            copy.Count,
            _lastActivityAt,
            copy.Count > 0 ? copy.Min(r => r.At) : null,
            _lastFlushAt,
            _lastMemoryId,
            _windowSeq,
            _flushing);
    }

    private static ServiceRound Clone(ServiceRound round) => new()
    {
        Seq = round.Seq,
        Key = round.Key,
        User = round.User,
        Assistants = new List<string>(round.Assistants),
        Tools = new List<string>(round.Tools),
        At = round.At,
        Frozen = round.Frozen,
        ContinuesKey = round.ContinuesKey,
    };

    /// <summary>
    /// Marks everything up to <paramref name="flushedSeq"/> summarized. Rounds that arrived during the flush have
    /// a larger seq, so they survive into the next window.
    /// </summary>
    public async Task CommitAsync(long flushedSeq, string memoryId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastFlushedSeq = flushedSeq;
            _windowSeq++;
            _lastMemoryId = memoryId;
            _lastFlushAt = _clock.GetUtcNow();
            _flushing = null;

            foreach (var round in _rounds.Where(r => r.Seq <= flushedSeq))
            {
                RememberKey(round.Key);
            }

            _rounds.RemoveAll(r => r.Seq <= flushedSeq);

            // Compact: the log is rewritten from the surviving rounds, so it cannot grow without bound.
            await RewriteLogAsync(ct).ConfigureAwait(false);
            await PersistStateAsync(force: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves past a record id that already belongs to another window. Jumps straight past every window the store
    /// already holds, so one collision is resolved in one step however many records the reset counter is behind.
    /// </summary>
    public async Task SkipWindowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _windowSeq = Math.Max(_windowSeq + 1, await _recoverWindowSeq(ct).ConfigureAwait(false));
            await PersistStateAsync(force: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RememberKey(string key)
    {
        _summarizedKeys.Remove(key);
        _summarizedKeys.Add(key);
        while (_summarizedKeys.Count > SummarizedKeyMemory)
        {
            _summarizedKeys.RemoveAt(0);
        }
    }

    /// <summary>One line for the round. The whole round is written so the replay can replace it by seq.</summary>
    private async Task AppendLogAsync(ServiceRound round, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new LogLine { Op = "upsert", Round = round }, Json) + "\n";
        await File.AppendAllTextAsync(_roundsPath, line, ct).ConfigureAwait(false);
        _logLines++;

        if (_logLines > _rounds.Count + LogSlack)
        {
            await RewriteLogAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RewriteLogAsync(CancellationToken ct)
    {
        var lines = _rounds.Select(r =>
            JsonSerializer.Serialize(new LogLine { Op = "upsert", Round = r }, Json));
        var text = string.Join('\n', lines);
        if (lines.Any())
        {
            text += "\n";
        }

        await WriteAtomicAsync(_roundsPath, text, ct).ConfigureAwait(false);
        _logLines = _rounds.Count;
    }

    /// <summary>
    /// Writes the state file, but not on every append: at most once a minute unless forced. A crash loses at most
    /// that minute, and <see cref="_lastActivityAt"/> is recoverable from the log.
    /// </summary>
    private async Task PersistStateAsync(bool force, CancellationToken ct)
    {
        // Until the window number is recovered, writing the state would persist a guessed counter that the next
        // process would then trust. The append log alone keeps the buffer safe in the meantime.
        if (_windowSeqUnknown)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        if (!force && now - _stateWrittenAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        var state = new StateDto
        {
            NextSeq = _nextSeq,
            LastFlushedSeq = _lastFlushedSeq,
            WindowSeq = _windowSeq,
            LastMemoryId = _lastMemoryId,
            LastFlushAt = _lastFlushAt,
            LastActivityAt = _lastActivityAt,
            FlushingRecordId = _flushing?.RecordId,
            FlushingSnapshotSeq = _flushing?.SnapshotSeq,
        };

        await WriteAtomicAsync(_statePath, JsonSerializer.Serialize(state, Json), ct).ConfigureAwait(false);
        _stateWrittenAt = now;
    }

    /// <summary>
    /// Writes through a temp file and an atomic replace. Overwriting in place can leave a half-written file after
    /// a crash, and a state file that cannot be parsed resets the window counter.
    /// </summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}