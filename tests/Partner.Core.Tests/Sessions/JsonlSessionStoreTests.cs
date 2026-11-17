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
    public Action? BeforeFlush { get; set; }

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
        BeforeFlush?.Invoke();

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

// Models a file reader that can observe appended bytes before the durability barrier completes.
internal sealed class LiveReadSink : IAppendOnlySink
{
    private readonly object _gate = new();
    private List<string> _lines = new();
    private long _durableLength;
    public Action? BeforeFlush { get; set; }
    public bool FailFlush { get; set; }

    public long DurableLength { get { lock (_gate) return _durableLength; } }
    public long Length { get { lock (_gate) return _lines.Count; } }
    public void Append(string record) { lock (_gate) _lines.Add(record); }
    public void FlushToDisk()
    {
        BeforeFlush?.Invoke();
        if (FailFlush) throw new IOException("simulated flush failure");
        lock (_gate) _durableLength = _lines.Count;
    }
    public void Rewrite(IReadOnlyList<string> records)
    {
        lock (_gate)
        {
            _lines = records.ToList();
            _durableLength = _lines.Count;
        }
    }
    public IReadOnlyList<string> ReadLines()
    {
        lock (_gate) return new LiveLines(_gate, _lines);
    }
    public long RepairTail() => 0;
    public void TruncateTo(long length)
    {
        lock (_gate)
        {
            _lines.RemoveRange((int)length, _lines.Count - (int)length);
            _durableLength = length;
        }
    }
    public void Dispose() { }

    private sealed class LiveLines(object gate, List<string> lines) : IReadOnlyList<string>
    {
        public int Count { get { lock (gate) return lines.Count; } }
        public string this[int index] { get { lock (gate) return lines[index]; } }
        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; ; i++)
            {
                string line;
                lock (gate)
                {
                    if (i >= lines.Count) yield break;
                    line = lines[i];
                }
                yield return line;
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Raw_reader_keeps_its_committed_watermark_during_append(bool fail, bool ranged)
    {
        var sink = new LiveReadSink();
        await using var store = new JsonlSessionStore(sink);
        await store.AppendAsync(Ev(body: "first"));
        await using var reader = (ranged ? store.ReadAsync(1, 10) : store.ReadAllAsync()).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("first", reader.Current.Body);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        sink.BeforeFlush = () => { entered.Set(); release.Wait(); };
        sink.FailFlush = fail;
        var append = Task.Run(() => store.AppendAsync(Ev(body: "second")));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "flush was not reached");
            Assert.Equal(1, store.LastSeq);
            Assert.False(await reader.MoveNextAsync());
        }
        finally
        {
            release.Set();
        }

        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => append);
            Assert.Equal(new long[] { 1 }, (await ReadAllAsync(store)).Select(e => e.Seq));
        }
        else
        {
            Assert.Equal(2, await append);
            Assert.Equal(new long[] { 1, 2 }, (await ReadAllAsync(store)).Select(e => e.Seq));
        }
    }

    [Fact]
    public async Task Buffered_raw_reads_exclude_visible_uncommitted_bytes_until_flush()
    {
        var sink = new LiveReadSink();
        await using var store = new JsonlSessionStore(sink, flushOnAppend: false);
        await store.AppendAsync(Ev(body: "first"));
        Assert.Empty(await ReadAllAsync(store));
        Assert.Empty((await store.ProjectAsync()).Items);
        await store.FlushAsync();

        await using var reader = store.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        await store.AppendAsync(Ev(body: "second"));
        Assert.False(await reader.MoveNextAsync());
        Assert.Single(await ReadAllAsync(store));
        await store.FlushAsync();
        Assert.Equal(new long[] { 1, 2 }, (await ReadAllAsync(store)).Select(e => e.Seq));
    }

    [Fact]
    public async Task Raw_reader_preserves_open_generation_across_purge()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev(body: "first"));
            await store.AppendAsync(Ev(body: "removed"));
            await store.AppendAsync(Ev(body: "third"));
            await using var reader = store.ReadAsync(1, 10).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(1, reader.Current.Seq);
            await store.PurgeAsync(2, 2);
            await store.AppendAsync(Ev(body: "after"));
            var oldSeqs = new List<long> { 1 };
            while (await reader.MoveNextAsync()) oldSeqs.Add(reader.Current.Seq);
            Assert.Equal(new long[] { 1, 2, 3 }, oldSeqs);
            Assert.Equal(new long[] { 1, 3, 4, 5 }, (await ReadAllAsync(store)).Select(e => e.Seq));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_purge_reopens_original_and_preserves_buffered_records(bool buffered)
    {
        var path = Path.Combine(_dir, "recover.jsonl");
        var attempts = 0;
        var sink = new FileAppendOnlySink(path, (_, _) => { attempts++; throw new IOException("locked"); },
            p => new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
        await using (var store = new JsonlSessionStore(sink, flushOnAppend: !buffered))
        {
            await store.AppendAsync(Ev(body: "first"));
            await store.AppendAsync(Ev(body: "second"));
            await Assert.ThrowsAsync<RewriteRecoveredException>(() => store.PurgeAsync(1, 1));
            Assert.Equal(21, attempts);
            Assert.False(store.Faulted);
            Assert.Equal(new[] { "first", "second" }, (await ReadAllAsync(store)).Select(e => e.Body));
            await store.AppendAsync(Ev(body: "after"));
            await store.FlushAsync();
        }
        await using var reopened = new JsonlSessionStore(new FileAppendOnlySink(path));
        Assert.Equal(new[] { "first", "second", "after" }, (await ReadAllAsync(reopened)).Select(e => e.Body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_reopen_failure_faults_store_before_any_further_write(bool replaced)
    {
        var path = Path.Combine(_dir, "fault.jsonl");
        var sink = new FileAppendOnlySink(path,
            (source, destination) => { if (replaced) File.Move(source, destination, true); else throw new IOException("locked"); },
            _ => throw new IOException("reopen failed"));
        await using var store = new JsonlSessionStore(sink);
        await store.AppendAsync(Ev(body: "first"));
        await store.AppendAsync(Ev(body: "second"));
        await Assert.ThrowsAnyAsync<Exception>(() => store.PurgeAsync(1, 1));
        Assert.True(store.Faulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Ev(body: "after")));
    }

    [Fact]
    public async Task Purge_includes_buffered_survivors()
    {
        var path = Path.Combine(_dir, "buffered.jsonl");
        await using (var store = new JsonlSessionStore(new FileAppendOnlySink(path), flushOnAppend: false))
        {
            await store.AppendAsync(Ev(body: "remove"));
            await store.AppendAsync(Ev(body: "keep"));
            await store.PurgeAsync(1, 1);
        }
        await using var reopened = new JsonlSessionStore(new FileAppendOnlySink(path));
        Assert.Contains((await ReadAllAsync(reopened)), e => e.Body == "keep");
    }

    [Fact]
    public async Task Raw_reader_honors_cancellation_between_events()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev(body: "first"));
            await store.AppendAsync(Ev(body: "second"));
            using var cancellation = new CancellationTokenSource();
            await using var reader = store.ReadAsync(1, 10, cancellation.Token).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_publishes_cached_append_only_after_successful_flush(bool fail)
    {
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        await store.AppendAsync(Ev(body: "first"));
        var oldView = await store.ProjectAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        sink.BeforeFlush = () => { entered.Set(); release.Wait(); };
        if (fail) sink.FailNextFlush();

        var append = Task.Run(() => store.AppendAsync(Ev(body: "second")));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "flush was not reached");
            var during = await store.ProjectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, store.LastSeq);
            Assert.Equal(1, during.LastSeq);
            Assert.Equal(new[] { "first" }, during.Items.Select(i => i.Body));
        }
        finally
        {
            release.Set();
        }

        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => append);
            Assert.True(store.Faulted);
            Assert.Equal(1, store.LastSeq);
            Assert.Single(sink.ReadLines());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ProjectAsync());
        }
        else
        {
            Assert.Equal(2, await append);
            var committed = await store.ProjectAsync();
            Assert.Equal(2, store.LastSeq);
            Assert.Equal(2, committed.LastSeq);
            Assert.Equal(new[] { "first", "second" }, committed.Items.Select(i => i.Body));
        }

        Assert.Equal(1, oldView.LastSeq);
        Assert.Equal(new[] { "first" }, oldView.Items.Select(i => i.Body));
    }

    [Fact]
    public async Task Projection_is_reused_until_the_log_moves()
    {
        // The projection replays the whole log. What matters is not that it is reused, but that it is *not*
        // reused one event too long.
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);

        await store.AppendAsync(Ev(body: "first"));
        Assert.Single((await store.ProjectAsync()).Items);

        await store.AppendAsync(Ev(body: "second"));

        // Both, not one: a cache that survives its own invalidation is the failure mode that matters, because
        // it is silent — the reader is simply told the wrong thing about a conversation.
        Assert.Equal(2, (await store.ProjectAsync()).Items.Count);
        Assert.Equal(new[] { "first", "second" }, (await store.ProjectAsync()).Items.Select(i => i.Body));
    }

    [Fact]
    public async Task Incremental_projection_preserves_old_views_across_chunk_boundaries_and_mutations()
    {
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        for (var i = 0; i < 1023; i++)
            await store.AppendAsync(Ev(body: $"old {i}") with { At = DateTimeOffset.UnixEpoch.AddDays(i % 2) });

        var before = await store.ProjectAsync();
        await store.AppendAsync(Ev(body: "boundary"));
        await store.AppendAsync(Ev(body: "next chunk"));
        var after = await store.ProjectAsync();
        Assert.Equal(1023, before.Items.Count);
        Assert.Equal("old 1022", before.Items[1022].Body);
        Assert.Equal(1025, after.Items.Count);
        Assert.Equal("boundary", after.Items[1023].Body);
        Assert.Equal("next chunk", after.Items[1024].Body);

        await store.AppendAsync(Ev(SessionEventKind.Edit, "edited") with { EditedFromSeq = 1 });
        await store.AppendAsync(Ev(SessionEventKind.Retract, "retracted") with
        {
            Meta = SessionRetraction.Single(2),
        });
        var changed = await store.ProjectAsync();
        Assert.Equal("edited", changed.Items[0].Body);
        Assert.Equal("old 0", after.Items[0].Body);
        Assert.DoesNotContain(changed.Items, item => item.Seq == 2);
        await store.AppendAsync(Ev(body: "after mutation"));
        var resumed = await store.ProjectAsync();
        var raw = new List<SessionEvent>();
        await foreach (var e in store.ReadAllAsync()) raw.Add(e);
        var rebuilt = SessionProjection.Build(raw);
        Assert.Equal(rebuilt.LastSeq, resumed.LastSeq);
        Assert.Equal(rebuilt.Items, resumed.Items);
    }

    [Fact]
    public async Task Forward_retraction_in_existing_log_still_hides_later_appends()
    {
        var sink = new FakeSink();
        sink.Append(SessionJson.Serialize(Ev(body: "first") with { Seq = 1 }));
        sink.Append(SessionJson.Serialize(Ev(SessionEventKind.Retract, "future") with
        {
            Seq = 2,
            Meta = SessionRetraction.Between(1, 4),
        }));
        sink.FlushToDisk();
        await using var store = new JsonlSessionStore(sink);
        Assert.Empty((await store.ProjectAsync()).Items);
        await store.AppendAsync(Ev(body: "hidden"));
        Assert.Empty((await store.ProjectAsync()).Items);
        await store.AppendAsync(Ev(body: "also hidden"));
        Assert.Empty((await store.ProjectAsync()).Items);
        await store.AppendAsync(Ev(body: "visible"));
        Assert.Equal("visible", Assert.Single((await store.ProjectAsync()).Items).Body);
    }

    [Fact]
    public async Task Incremental_projection_matches_a_full_rebuild_through_random_mutations()
    {
        // A cached view must match a full replay after every mutation.
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);
        var random = new Random(20261030);
        var kinds = new[]
        {
            SessionEventKind.User, SessionEventKind.Assistant, SessionEventKind.Thinking,
            SessionEventKind.ToolCall, SessionEventKind.ToolResult,
        };

        for (var step = 0; step < 120; step++)
        {
            var lastSeq = store.LastSeq;
            var operation = random.Next(100);

            if (operation < 50 || lastSeq == 0)
            {
                await store.AppendAsync(Ev(
                    kinds[random.Next(kinds.Length)],
                    $"body {step} {new string('x', random.Next(0, 40))}"));
            }
            else if (operation < 65)
            {
                var target = random.NextInt64(1, lastSeq + 1);
                await store.AppendAsync(Ev(SessionEventKind.Edit, $"edited {step}") with
                {
                    EditedFromSeq = target,
                });
            }
            else if (operation < 80)
            {
                var from = random.NextInt64(1, lastSeq + 1);
                var to = Math.Min(lastSeq, from + random.Next(0, 3));
                await store.AppendAsync(Ev(SessionEventKind.Retract, $"retracted {step}") with
                {
                    Meta = SessionRetraction.Between(from, to),
                });
            }
            else
            {
                var from = random.NextInt64(1, lastSeq + 1);
                var to = Math.Min(lastSeq, from + random.Next(0, 3));
                await store.PurgeAsync(from, to);
            }

            var actual = await store.ProjectAsync();
            var full = SessionProjection.Build(await ReadAllAsync(store));

            Assert.Equal(full.LastSeq, actual.LastSeq);
            Assert.Equal(full.Items.Count, actual.Items.Count);
            for (var i = 0; i < full.Items.Count; i++)
            {
                Assert.Equal(full.Items[i], actual.Items[i]);
            }
        }
    }

    private static async Task<List<SessionEvent>> ReadAllAsync(ISessionStore store)
    {
        var events = new List<SessionEvent>();
        await foreach (var e in store.ReadAllAsync())
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Projection_is_rebuilt_after_a_purge_rewrites_the_log()
    {
        // A purge rewrites the file rather than appending, so an append-only invalidation would miss it and
        // leave deleted text readable through the cache.
        //
        // The tombstone that PurgeAsync appends invalidates on its own, so this would pass even without the
        // explicit bump after Rewrite. That bump closes the window between the rewrite and the tombstone, which
        // is not reachable from here.
        var sink = new FakeSink();
        await using var store = new JsonlSessionStore(sink);

        await store.AppendAsync(Ev(body: "keep me"));
        var secretSeq = await store.AppendAsync(Ev(body: "remove me from the log"));
        await store.AppendAsync(Ev(body: "keep me too"));
        await store.ProjectAsync();

        await store.PurgeAsync(secretSeq, secretSeq);

        var bodies = (await store.ProjectAsync()).Items.Select(i => i.Body).ToList();
        Assert.DoesNotContain("remove me from the log", bodies);
        Assert.Equal(2, bodies.Count);
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
        // The log is one JSON record per line; a body's newlines are escaped by the serializer. Flattening them
        // would destroy a markdown reply's structure.
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
            Assert.Empty((await store.ProjectAsync()).Items);

            await store.FlushAsync();

            Assert.Single((await store.ProjectAsync()).Items);

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
    public async Task Indexed_reads_survive_reopen_tail_repair_and_purge()
    {
        var path = Path.Combine(_dir, "session", "events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = Enumerable.Range(1, 600)
            .Select(i => SessionJson.Serialize(Ev(body: $"消息 😀 {i}") with { Seq = i }));
        await File.WriteAllTextAsync(path, string.Join("\r\n", lines) + "\r\n{torn");

        var (store, repaired) = JsonlSessionStore.Open(_dir);
        Assert.Equal(5, repaired);
        await using (store)
        {
            async Task<long[]> Read(long from, int take)
            {
                var result = new List<long>();
                await foreach (var e in store.ReadAsync(from, take)) result.Add(e.Seq);
                return result.ToArray();
            }

            Assert.Equal(Enumerable.Range(491, 100).Select(i => (long)i), await Read(491, 100));
            await store.PurgeAsync(100, 400);
            Assert.Equal(Enumerable.Range(491, 100).Select(i => (long)i), await Read(491, 100));
            Assert.Equal(new long[] { 99, 401, 402 }, await Read(99, 3));
            Assert.Equal(602, await store.AppendAsync(Ev(body: "after purge")));
        }

        var (reopened, _) = JsonlSessionStore.Open(_dir);
        await using (reopened)
        {
            var seqs = new List<long>();
            await foreach (var e in reopened.ReadAsync(599, 10)) seqs.Add(e.Seq);
            Assert.Equal(new long[] { 599, 600, 601, 602 }, seqs);
        }
    }

    [Fact]
    public async Task Range_reader_does_not_hold_writer_gate_across_yields()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await store.AppendAsync(Ev());
            await using var reader = store.ReadAllAsync().GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            await store.AppendAsync(Ev(body: "next"));
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
