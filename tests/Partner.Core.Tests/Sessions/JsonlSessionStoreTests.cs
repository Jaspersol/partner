using Partner.Core.Contracts;
using Partner.Core.Sessions;
using Partner.Core.Storage;

namespace Partner.Core.Tests.Sessions;

/// <summary>Test double: an append-only sink whose durability barrier can be made to fail.</summary>
internal sealed class FakeSink : IAppendOnlySink
{
    private readonly List<string> _lines = new();
    private readonly List<string> _buffer = new();
    private bool _failNextFlush;

    public long DurableLength { get; private set; }

    public long Length => DurableLength + _buffer.Count;

    public void FailNextFlush() => _failNextFlush = true;

    public void Append(string record)
    {
        if (record.Contains('\n'))
        {
            throw new ArgumentException("Record must not contain newline characters.", nameof(record));
        }

        _buffer.Add(record);
    }

    public void FlushToDisk()
    {
        if (_failNextFlush)
        {
            _failNextFlush = false;
            throw new IOException("simulated flush failure");
        }

        _lines.AddRange(_buffer);
        DurableLength += _buffer.Count;
        _buffer.Clear();
    }

    public IReadOnlyList<string> ReadLines() => _lines.ToList();

    public long RepairTail() => 0;

    public void TruncateTo(long length)
    {
        // Each record counts as one unit in this double.
        var keep = (int)Math.Min(length, _lines.Count);
        if (keep < _lines.Count)
        {
            _lines.RemoveRange(keep, _lines.Count - keep);
        }

        DurableLength = _lines.Count;
        _buffer.Clear();
    }

    public void Dispose()
    {
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

public sealed class JsonlSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SessionEvent Ev(SessionEventKind kind = SessionEventKind.User, string body = "hi") =>
        new() { Kind = kind, Source = InputSource.Text, Body = body };

    [Fact]
    public async Task Append_assigns_monotonic_seq_starting_at_one()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            Assert.Equal(1, await store.AppendAsync(Ev()));
            Assert.Equal(2, await store.AppendAsync(Ev()));
            Assert.Equal(3, await store.AppendAsync(Ev()));
            Assert.Equal(3, store.LastSeq);
        }
    }

    [Fact]
    public async Task Append_rejects_caller_supplied_wrong_seq()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AppendAsync(Ev() with { Seq = 5 }));
        }
    }

    [Fact]
    public async Task Reopen_continues_seq_without_reuse()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev(body: "a"));
            await store.AppendAsync(Ev(body: "b"));
        }

        var (reopened, repaired) = JsonlSessionStore.Open(_dir);
        await using (reopened)
        {
            Assert.Equal(0, repaired);
            Assert.Equal(2, reopened.LastSeq);
            Assert.Equal(3, await reopened.AppendAsync(Ev(body: "c")));

            var bodies = new List<string>();
            await foreach (var e in reopened.ReadAllAsync())
            {
                bodies.Add(e.Body);
            }

            Assert.Equal(new[] { "a", "b", "c" }, bodies);
        }
    }

    [Fact]
    public async Task Append_rejects_multiline_body()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAsync(Ev(body: "line1\nline2")));
        }
    }

    [Fact]
    public async Task Edit_requires_edited_from_seq_and_non_edit_forbids_it()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev());

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAsync(Ev(SessionEventKind.Edit, "x")));

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAsync(Ev() with { EditedFromSeq = 1 }));

            // Forward reference is rejected too.
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAsync(Ev(SessionEventKind.Edit, "x") with { EditedFromSeq = 99 }));
        }
    }

    [Fact]
    public async Task Timestamp_defaults_to_clock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
        var (store, _) = JsonlSessionStore.Open(_dir, clock);
        await using (store)
        {
            await store.AppendAsync(Ev());

            var all = new List<SessionEvent>();
            await foreach (var e in store.ReadAllAsync())
            {
                all.Add(e);
            }

            Assert.Equal(clock.GetUtcNow(), all[0].At);
        }
    }

    [Fact]
    public async Task Torn_tail_is_repaired_on_open()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev(body: "good1"));
            await store.AppendAsync(Ev(body: "good2"));
        }

        // Simulate a crash mid-append: a partial line with no trailing newline.
        var path = Path.Combine(_dir, "session", "events.jsonl");
        await File.AppendAllTextAsync(path, "{\"seq\":3,\"ki");

        var (reopened, repaired) = JsonlSessionStore.Open(_dir);
        await using (reopened)
        {
            Assert.True(repaired > 0, "torn tail should be truncated");

            var bodies = new List<string>();
            await foreach (var e in reopened.ReadAllAsync())
            {
                bodies.Add(e.Body);
            }

            Assert.Equal(new[] { "good1", "good2" }, bodies);
            Assert.Equal(3, await reopened.AppendAsync(Ev(body: "good3")));
        }
    }

    [Fact]
    public async Task Flush_failure_faults_store_and_blocks_further_writes()
    {
        var sink = new FakeSink();
        var store = new JsonlSessionStore(sink, flushOnAppend: false);
        await using (store)
        {
            await store.AppendAsync(Ev(body: "buffered"));
            Assert.False(store.Faulted);

            sink.FailNextFlush();

            await Assert.ThrowsAsync<IOException>(() => store.FlushAsync());
            Assert.True(store.Faulted);
            Assert.Equal(0, store.LastSeq);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Ev(body: "after")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.FlushAsync());
        }
    }

    [Fact]
    public async Task Buffered_appends_are_invisible_until_flush()
    {
        var sink = new FakeSink();
        var store = new JsonlSessionStore(sink, flushOnAppend: false);
        await using (store)
        {
            await store.AppendAsync(Ev(body: "pending"));

            var before = new List<SessionEvent>();
            await foreach (var e in store.ReadAllAsync())
            {
                before.Add(e);
            }

            Assert.Empty(before);

            await store.FlushAsync();

            var after = new List<SessionEvent>();
            await foreach (var e in store.ReadAllAsync())
            {
                after.Add(e);
            }

            Assert.Single(after);
            Assert.Equal("pending", after[0].Body);
        }
    }

    [Fact]
    public async Task Read_respects_from_seq_and_take()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            for (var i = 1; i <= 5; i++)
            {
                await store.AppendAsync(Ev(body: $"m{i}"));
            }

            var page = new List<long>();
            await foreach (var e in store.ReadAsync(fromSeq: 2, take: 2))
            {
                page.Add(e.Seq);
            }

            Assert.Equal(new long[] { 2, 3 }, page);
        }
    }

    [Fact]
    public async Task Parallel_appends_produce_unique_contiguous_seqs()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            const int writers = 8;
            const int perWriter = 25;

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    await store.AppendAsync(Ev(body: $"w{w}-{i}"));
                }
            }));

            await Task.WhenAll(tasks);

            var seqs = new List<long>();
            await foreach (var e in store.ReadAllAsync())
            {
                seqs.Add(e.Seq);
            }

            Assert.Equal(writers * perWriter, seqs.Count);
            Assert.Equal(Enumerable.Range(1, writers * perWriter).Select(i => (long)i), seqs);
        }
    }
}
