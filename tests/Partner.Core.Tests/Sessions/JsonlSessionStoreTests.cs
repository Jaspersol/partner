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

    /// <summary>Swaps in a new set of lines: the double stands in for the real file's atomic replace.</summary>
    public void Rewrite(IReadOnlyList<string> records)
    {
        _lines.Clear();
        _lines.AddRange(records);
        _buffer.Clear();
        DurableLength = _lines.Count;
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
    public async Task A_purge_takes_the_lines_out_and_leaves_a_content_free_marker()
    {
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        var one = await store.AppendAsync(Ev(body: "first secret"));
        var two = await store.AppendAsync(Ev(body: "second secret"));
        var three = await store.AppendAsync(Ev(body: "third secret"));

        var purged = await store.PurgeAsync(two, two);

        Assert.Equal(1, purged.RemovedEvents);
        Assert.Equal(0, purged.RemovedRevisions);

        var log = string.Join("\n", sink.ReadLines());
        Assert.DoesNotContain("second secret", log, StringComparison.Ordinal);
        Assert.Contains("first secret", log, StringComparison.Ordinal);
        Assert.Contains("third secret", log, StringComparison.Ordinal);

        // What stays behind is the range, not the content: a removal that is still auditable, without being a
        // copy of what was removed.
        Assert.Contains("purge #2..#2", log, StringComparison.Ordinal);

        var view = await store.ProjectAsync();
        Assert.Equal(new[] { one, three }, view.Items.Select(i => i.Seq).ToArray());
    }

    [Fact]
    public async Task A_purge_takes_the_revisions_of_those_events_with_it()
    {
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        var one = await store.AppendAsync(Ev(body: "the original text"));
        await store.AppendAsync(Ev(SessionEventKind.Edit, "the revised text") with { EditedFromSeq = one });

        await store.PurgeAsync(one, one);

        var log = string.Join("\n", sink.ReadLines());
        Assert.DoesNotContain("the original text", log, StringComparison.Ordinal);
        Assert.DoesNotContain("the revised text", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_purge_never_hands_a_seq_out_twice()
    {
        // Memory records point back at session seqs (MemoryRecord.SourceEventSeq), so a reused seq would
        // silently re-attribute a memory to a different event.
        long purgedTo;
        long marker;

        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var one = await store.AppendAsync(Ev(body: "first"));
            purgedTo = await store.AppendAsync(Ev(body: "second"));
            marker = (await store.PurgeAsync(one, purgedTo)).TombstoneSeq;

            Assert.True(store.LastSeq >= marker);
        }

        // The high-water mark is read back from the file, not remembered from the previous process.
        var (reopened, _) = JsonlSessionStore.Open(_dir);
        await using (reopened)
        {
            Assert.True(reopened.LastSeq > purgedTo);
            Assert.True(await reopened.AppendAsync(Ev(body: "after")) > purgedTo);
        }
    }

    [Fact]
    public async Task A_purge_leaves_retractions_it_was_not_asked_about_alone()
    {
        // Compaction hides folded history with a retraction; it is lossless by design, so a purge of a
        // different range must not sweep it up.
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        var folded = await store.AppendAsync(Ev(body: "folded away"));
        await store.AppendAsync(Ev(body: "kept"));

        await store.AppendAsync(Ev(SessionEventKind.Retract, "folded") with
        {
            Meta = SessionRetraction.Single(folded),
        });

        var removed = await store.AppendAsync(Ev(body: "removed on purpose"));
        await store.PurgeAsync(removed, removed);

        var log = string.Join("\n", sink.ReadLines());
        Assert.Contains("folded away", log, StringComparison.Ordinal);
        Assert.Contains("kept", log, StringComparison.Ordinal);
        Assert.DoesNotContain("removed on purpose", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_a_range_that_is_not_in_the_log_changes_nothing()
    {
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        await store.AppendAsync(Ev(body: "only one"));
        var before = sink.ReadLines().Count;

        var purged = await store.PurgeAsync(50, 60);

        Assert.Equal(0, purged.RemovedEvents);
        Assert.Equal(before, sink.ReadLines().Count);
    }

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
    public async Task A_multiline_body_round_trips_through_the_file()
    {
        // The log is one JSON record per line; a body's newlines belong to the body and are escaped by the
        // serializer. Flattening them here is what used to destroy a markdown reply's structure.
        const string Markdown = "## 标题\n\n- 一\n- 二\n\n```json\n{ \"a\": 1 }\n```";

        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev(body: Markdown));
        }

        // One physical line per record, and the body comes back exactly as it went in.
        var lines = File.ReadAllLines(Path.Combine(_dir, "session", "events.jsonl"));
        Assert.Single(lines);

        var (reopened, _) = JsonlSessionStore.Open(_dir);
        await using (reopened)
        {
            var events = new List<SessionEvent>();
            await foreach (var e in reopened.ReadAllAsync())
            {
                events.Add(e);
            }

            Assert.Equal(Markdown, Assert.Single(events).Body);
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
