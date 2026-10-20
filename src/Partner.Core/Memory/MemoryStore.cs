using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Partner.Core.Memory;

/// <summary>
/// SQLite-backed memory store (design doc §2.5.6): FTS5 coarse filter → explainable re-rank, promotion
/// before trimming, four-step privacy delete, and offline degradation that never loses records.
/// </summary>
public sealed class MemoryStore : IMemoryStore
{
    private readonly SqliteConnection _db;
    private readonly MemoryStoreOptions _options;
    private readonly TimeProvider _clock;

    public MemoryStore(string databasePath, MemoryStoreOptions? options = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _options = options ?? new MemoryStoreOptions();
        _clock = clock ?? TimeProvider.System;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _db.Open();

        // Whether the file already held a store decides what the DDL means: for a new one it *is* the schema, for
        // an old one it only adds what can be added idempotently, and the rest comes from the migration steps.
        var existing = TableExists("memories");
        Execute(MemorySchema.Ddl);

        try
        {
            if (existing)
            {
                SchemaVersion = Migrate();
            }
            else
            {
                // A new store is born at the current version: every step's work is already in the DDL, so running
                // them would only replay history against a schema that never had the older shape.
                SetUserVersion(MemorySchema.Version);
                SchemaVersion = MemorySchema.Version;
            }

            // Last, once every path above has established body_cjk: a new store from the DDL, an old one from the
            // migration. The triggers name that column, and SQLite resolves a trigger body when the statement is
            // prepared — creating them any earlier makes an old store fail to open at all (measured).
            Execute(MemorySchema.CjkTriggers);
        }
        catch
        {
            // Refusing to open must not leave the file held: the caller has no store to dispose, and a connection
            // this process will not use would keep the workspace locked for whoever does open it.
            _db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The schema version this store is at, after opening. Equal to <see cref="MemorySchema.Version"/> unless a
    /// migration refused to run.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Brings an existing store up to <see cref="MemorySchema.Version"/>, one step at a time, stamping the version
    /// after each step so a crash halfway leaves the store at the last step that completed rather than at zero.
    /// </summary>
    private int Migrate()
    {
        var version = ReadUserVersion();

        // Reading a store written by a newer build would mean guessing at a schema this code has never seen —
        // and writing to it could lose columns it added. Refusing is the only answer that cannot corrupt it.
        if (version > MemorySchema.Version)
        {
            throw new InvalidOperationException(
                $"memory store is at schema version {version}, this build knows {MemorySchema.Version} — "
                + "it was written by a newer version of the app; open it with that one");
        }

        foreach (var step in MemorySchema.Migrations.Where(m => m.To > version).OrderBy(m => m.To))
        {
            foreach (var statement in step.Statements)
            {
                try
                {
                    Execute(statement);
                }
                catch (SqliteException) when (step.TolerateFailures)
                {
                    // "duplicate column" — the statement was already applied to this store. Only a step that says
                    // so in advance is allowed to be satisfied by a failure; the rest would be a lie.
                }
            }

            // SQL first, then the part SQL cannot express. A failure here propagates: the flag above covers the
            // statements, not a half-written backfill.
            step.Data?.Invoke(_db);

            SetUserVersion(step.To);
            version = step.To;
        }

        return version;
    }

    private bool TableExists(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    private int ReadUserVersion()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void SetUserVersion(int version)
    {
        // PRAGMA does not take parameters; the value is an int from this assembly, never user text.
        Execute($"PRAGMA user_version = {version}");
    }

    /// <summary>How often the write path may run a promotion pass; the maintenance pass covers the rest.</summary>
    internal static readonly TimeSpan PromotionThrottle = TimeSpan.FromSeconds(30);

    private DateTimeOffset _lastPromotionPass = DateTimeOffset.MinValue;

    private async Task PromoteIfDueAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (now - _lastPromotionPass < PromotionThrottle && now >= _lastPromotionPass)
        {
            return;
        }

        _lastPromotionPass = now;
        await PromoteEligibleAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> WriteAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Design doc §2.5.6 ③: every write path consolidates eligible working records before committing. A pass
        // reads the table, so it runs at most once per PromotionThrottle, not once per record.
        await PromoteIfDueAsync(ct).ConfigureAwait(false);

        // A newer statement about the same thing retires the older one. Doing it here rather than at recall
        // means the list a person reads already shows the truth, instead of the prompt quietly hiding one half
        // of a contradiction. The retired record is marked, not deleted: it stays visible and editable.
        await SupersedeConflictingAsync(record, ct).ConfigureAwait(false);

        await InsertAsync(record, ct).ConfigureAwait(false);
        await LogAsync(record.RecordId, "write", record.Tier.ToString(), ct).ConfigureAwait(false);
        return record.RecordId;
    }

    /// <summary>
    /// Retires live records that the incoming one contradicts.
    /// <para>
    /// Only records in the same tier are compared: a semantic fact does not contradict a working note, and
    /// demotion moves things between tiers anyway, so comparing across them would invalidate a record for
    /// the wrong reason.
    /// </para>
    /// </summary>
    private async Task SupersedeConflictingAsync(MemoryRecord incoming, CancellationToken ct)
    {
        // Two coarse filters for the same reason a recall needs two: a contradiction shares a subject and
        // differs on one point, so it shares no whole substring — matching the sentence finds nothing and
        // leaves both halves of the disagreement in the store.
        //
        // Bigrams for CJK, and the FTS expression for Latin, which has no bigrams to cut but a real
        // tokenizer. Both are built even for a body that is mostly one script: a mixed body's English half is
        // invisible to the bigram index and its Chinese half to the Latin one, so dropping either would leave
        // half of it unsearchable. The decision is still made by word overlap below.
        var match = BuildMatchExpression(incoming.Body);
        var cjkMatch = CjkText.MatchExpression(incoming.Body);

        var candidates = await LoadCandidatesAsync(match, cjkMatch, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        foreach (var candidate in candidates)
        {
            if (candidate.Tier != incoming.Tier)
            {
                continue;
            }

            if (!MemoryLifecycle.Conflicts(incoming.Body, candidate.Body))
            {
                continue;
            }

            await using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE memories SET valid_to = $now, superseded_by = $by, edited_by = 'Agent' "
                + "WHERE record_id = $id AND valid_to IS NULL";
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$by", incoming.RecordId);
            cmd.Parameters.AddWithValue("$id", candidate.RecordId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await LogAsync(candidate.RecordId, "supersede", incoming.RecordId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks what was just handed out as used.
    /// <para>
    /// Both happen on the recall path because that is the only place "used" is observable, and demotion needs a
    /// moment when the store is already open. A record nobody found for three months is not something to keep
    /// handing to a model at full trust.
    /// </para>
    /// </summary>
    private async Task MaintainAsync(IReadOnlyList<string> recalled, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // One statement per chunk instead of one per hit: the recall path is on the critical path of every turn.
        // Demotion is not here any more — it scans the whole table, and the maintenance pass already runs it.
        const int ChunkSize = 400;
        for (var from = 0; from < recalled.Count; from += ChunkSize)
        {
            var chunk = recalled.Skip(from).Take(ChunkSize).ToList();

            await using var cmd = _db.CreateCommand();
            var names = new List<string>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                names.Add("$r" + i);
                cmd.Parameters.AddWithValue("$r" + i, chunk[i]);
            }

            cmd.CommandText = $"UPDATE memories SET last_recalled_at = $now WHERE record_id IN ({string.Join(',', names)})";
            cmd.Parameters.AddWithValue("$now", Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops each idle record one tier: Semantic → Episodic → Working. Working then falls to the existing
    /// trim, so a record nobody has used in a year eventually leaves on its own.
    /// <para>
    /// <see cref="MemoryScope.Retain"/> records are skipped: retention is exactly the promise that the demotion
    /// rule may not break, and it is the only way a person can protect a fact from the clock.
    /// </para>
    /// <para>
    /// Procedural goes to Episodic rather than to Working. It used to fall through the CASE into Working, which
    /// skipped a level — and Procedural is the only tier that records a way of doing something rather than a
    /// fact, so dropping it two notches loses the part that made it worth keeping.
    /// </para>
    /// </summary>
    public async Task<int> DemoteIdleAsync(CancellationToken ct = default)
    {
        var demoted = 0;
        await DemoteIdleAsync(_clock.GetUtcNow(), ct, d => demoted = d).ConfigureAwait(false);
        return demoted;
    }

    private async Task DemoteIdleAsync(DateTimeOffset now, CancellationToken ct, Action<int>? reported = null)
    {
        var cutoff = now - MemoryLifecycle.IdleBeforeDemotion;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "UPDATE memories SET tier = CASE tier WHEN 'Semantic' THEN 'Episodic' WHEN 'Procedural' THEN 'Episodic' ELSE 'Working' END "
            + "WHERE valid_to IS NULL AND tier != 'Working' AND scope != 'Retain' "
            + "AND (last_recalled_at IS NULL OR last_recalled_at <= $cutoff)";
        cmd.Parameters.AddWithValue("$cutoff", Iso(cutoff));

        var demoted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        reported?.Invoke(demoted);
        if (demoted > 0)
        {
            await LogAsync(
                $"demote:{demoted}",
                "demote",
                $"idle > {MemoryLifecycle.IdleBeforeDemotion.TotalDays:0} 天",
                ct).ConfigureAwait(false);
        }
    }

    public async Task WriteBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records as IReadOnlyCollection<MemoryRecord> ?? records.ToList();
        if (list.Count == 0)
        {
            return;
        }

        await PromoteIfDueAsync(ct).ConfigureAwait(false);

        // Embedded before the transaction opens: computing a vector can be a network call, and a write
        // transaction held open across it blocks every other writer for as long as the provider takes.
        var vectors = new Dictionary<string, float[]?>(StringComparer.Ordinal);
        if (_options.Embeddings.Dimensions > 0)
        {
            foreach (var record in list)
            {
                vectors[record.RecordId] = await TryEmbedAsync(record.Body, ct).ConfigureAwait(false);
            }
        }

        await using var tx = _db.BeginTransaction();
        foreach (var record in list)
        {
            await InsertAsync(record, ct, tx, embedded: vectors).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        await LogAsync($"batch:{list.Count}", "write-batch", $"{list.Count} records", ct).ConfigureAwait(false);
    }

    public async Task<MemoryRecord?> GetAsync(string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE record_id = $id";
        cmd.Parameters.AddWithValue("$id", recordId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<MemoryRecord?> FindBySourceAsync(long sourceEventSeq, CancellationToken ct = default)
    {
        // Ordered so a repeated write of the same event finds the *oldest* one: a second write would otherwise
        // find its own predecessor and report success while leaving two rows behind.
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE source_event_seq = $seq AND valid_to IS NULL ORDER BY created_at, record_id";
        cmd.Parameters.AddWithValue("$seq", sourceEventSeq);

        SourceQueries++;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<MemoryRecord>> FindBySourceRangeAsync(
        long fromSeq,
        long toSeq,
        CancellationToken ct = default)
    {
        if (toSeq < fromSeq)
        {
            return Array.Empty<MemoryRecord>();
        }

        await using var cmd = _db.CreateCommand();
        cmd.CommandText =
            $"{SelectColumns} WHERE source_event_seq BETWEEN $from AND $to AND valid_to IS NULL "
            + "ORDER BY source_event_seq, created_at, record_id";
        cmd.Parameters.AddWithValue("$from", fromSeq);
        cmd.Parameters.AddWithValue("$to", toSeq);

        SourceQueries++;
        var records = new List<MemoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    /// <summary>
    /// How many source lookups this store has run, counting each query once however many rows it returned.
    /// <para>
    /// Exists for the deletion cascade's own test: "a thousand-event deletion is one query, not a thousand" is a
    /// claim about the statement count, and a stopwatch cannot tell the two apart on a warm page cache.
    /// </para>
    /// </summary>
    internal int SourceQueries { get; private set; }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        var filter = query ?? new MemoryQuery();

        await using var cmd = _db.CreateCommand();
        var sql = new StringBuilder(SelectColumns).Append(" WHERE 1=1");
        if (!filter.IncludeInvalidated)
        {
            sql.Append(" AND valid_to IS NULL");
        }

        if (filter.Tier is not null)
        {
            sql.Append(" AND tier = $tier");
            cmd.Parameters.AddWithValue("$tier", filter.Tier.Value.ToString());
        }

        if (filter.Scope is not null)
        {
            sql.Append(" AND scope = $scope");
            cmd.Parameters.AddWithValue("$scope", filter.Scope.Value.ToString());
        }

        sql.Append(" ORDER BY created_at, record_id LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, filter.Limit));
        cmd.CommandText = sql.ToString();

        var results = new List<MemoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<MemoryHit>> RecallAsync(string query, int? limit = null, CancellationToken ct = default) =>
        await RecallAsync(query, limit, excludeSourceSeqs: null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string query,
        int? limit,
        IReadOnlySet<long>? excludeSourceSeqs,
        CancellationToken ct = default)
    {
        var take = Math.Max(1, limit ?? _options.RecallLimit);

        // Two coarse filters, because one index cannot serve both scripts. unicode61 has no Chinese word
        // segmentation, so a CJK question is matched against the bigrams in the second index instead (CjkText):
        // 我不太喜欢被打断 and 用户不喜欢被反复确认 share 喜欢/欢被, and that overlap is enough to bring the
        // record in. Both expressions are built for every query — a mixed query like 部署 deployment carries
        // words only one of the indexes can see, and choosing by "does it contain CJK" dropped the other half.
        // The scorer still decides relevance; this is only a coarse filter.
        var match = BuildMatchExpression(query);
        var cjkMatch = CjkText.MatchExpression(query);

        if (match is null && cjkMatch is null)
        {
            return Array.Empty<MemoryHit>();
        }

        var candidates = await LoadCandidatesAsync(match, cjkMatch, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return Array.Empty<MemoryHit>();
        }
        var embeddings = await LoadEmbeddingsAsync(candidates.Select(c => c.RecordId).ToList(), ct)
            .ConfigureAwait(false);
        var queryEmbedding = await TryEmbedAsync(query, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        // Excluded before scoring rather than after taking the top N: a filtered-out record that still occupied
        // a slot would silently shorten the answer, and the caller asked for N useful hits, not N candidates
        // minus however many it already knew.
        var eligible = excludeSourceSeqs is null
            ? candidates
            : candidates.Where(c => c.SourceEventSeq is not { } seq || !excludeSourceSeqs.Contains(seq)).ToList();

        if (eligible.Count == 0)
        {
            return Array.Empty<MemoryHit>();
        }

        var seenBodies = new HashSet<string>(StringComparer.Ordinal);
        var result = eligible
            .Select(candidate =>
            {
                embeddings.TryGetValue(candidate.RecordId, out var candidateEmbedding);
                var score = _options.Scorer.Score(candidate, query, queryEmbedding, candidateEmbedding, now);
                return new MemoryHit(
                    candidate.RecordId,
                    candidate.Body,
                    score,
                    _options.Scorer.Explain(score),
                    candidate.CreatedAt,
                    candidate.Tier,
                    candidate.Scope,
                    now,
                    candidate.SourceEventSeq);
            })
            .OrderByDescending(h => h.Score.Total)
            .ThenBy(h => h.RecordId, StringComparer.Ordinal)
            // The same sentence can live in two rows (a promotion leaves the working row behind next to its episodic
            // copy). Handing the model both is two slots for one fact; the better-scored row stands for it.
            .Where(hit => hit.Score.Total >= _options.MinScore)
            .Where(hit => seenBodies.Add(MemoryLifecycle.Normalize(hit.Body)))
            .Take(take)
            .ToList();

        // What was actually handed out is what counts as used — not what merely became a candidate.
        await MaintainAsync(result.Select(hit => hit.RecordId).ToList(), ct).ConfigureAwait(false);
        return result;
    }

    public async Task<InjectionResult> InjectAsync(string query, CancellationToken ct = default) =>
        await InjectAsync(query, excludeSourceSeqs: null, ct).ConfigureAwait(false);

    public async Task<InjectionResult> InjectAsync(
        string query,
        IReadOnlySet<long>? excludeSourceSeqs,
        CancellationToken ct = default)
    {
        var hits = await RecallAsync(query, _options.RecallLimit, excludeSourceSeqs, ct).ConfigureAwait(false);

        var kept = new List<MemoryHit>();
        var tokens = 0;
        var truncated = false;

        foreach (var hit in hits)
        {
            var cost = _options.Tokens.Count(hit.Body);
            if (tokens + cost > _options.InjectionTokenLimit)
            {
                truncated = true;
                break;
            }

            tokens += cost;
            kept.Add(hit);
        }

        return new InjectionResult(kept, tokens, truncated);
    }

    public async Task<string> ReviseAsync(string recordId, string newBody, Actor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(newBody);

        var existing = await GetAsync(recordId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown memory record '{recordId}'.");

        var now = _clock.GetUtcNow();

        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO memory_revisions(record_id, at, before, after, actor) VALUES ($id, $at, $before, $after, $actor)";
            cmd.Parameters.AddWithValue("$id", recordId);
            cmd.Parameters.AddWithValue("$at", Iso(now));
            cmd.Parameters.AddWithValue("$before", existing.Body);
            cmd.Parameters.AddWithValue("$after", newBody);
            cmd.Parameters.AddWithValue("$actor", actor.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE memories SET body = $body, body_cjk = $cjk, edited_by = $actor WHERE record_id = $id";
            cmd.Parameters.AddWithValue("$body", newBody);
            cmd.Parameters.AddWithValue("$cjk", CjkText.Index(newBody));
            cmd.Parameters.AddWithValue("$actor", actor.ToString());
            cmd.Parameters.AddWithValue("$id", recordId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await LogAsync(recordId, "revise", actor.ToString(), ct).ConfigureAwait(false);
        return recordId;
    }

    public async Task SoftDeleteAsync(string recordId, Actor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        var now = _clock.GetUtcNow();

        // Step 1+3: soft invalidate and purge the text in one statement (the FTS triggers drop the old body and
        // its bigrams — both indexes are projections, and a purge that left one of them holding the words would
        // not be a purge).
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE memories SET valid_to = $now, body = '', body_cjk = '', pending_compact = 1, edited_by = $actor WHERE record_id = $id";
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$actor", actor.ToString());
            cmd.Parameters.AddWithValue("$id", recordId);

            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException($"Unknown memory record '{recordId}'.");
            }
        }

        // Step 2: drop the vector index entry.
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM memory_embeddings WHERE record_id = $id";
            cmd.Parameters.AddWithValue("$id", recordId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await LogAsync(recordId, "delete", actor.ToString(), ct).ConfigureAwait(false);
    }

    public async Task<int> PromoteEligibleAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - _options.PromotionAge;

        var ids = await QueryIdsAsync(
            "SELECT record_id FROM memories WHERE tier = 'Working' AND consolidated_at IS NULL AND created_at <= $cutoff",
            ("$cutoff", Iso(cutoff)),
            ct).ConfigureAwait(false);

        var promoted = 0;

        foreach (var id in ids)
        {
            var working = await GetAsync(id, ct).ConfigureAwait(false);
            if (working is null)
            {
                continue;
            }

            // Promotion preserves the content: an episodic copy is written, then the working row is marked
            // consolidated. The copy is what survives trimming (design doc §2.5.6 ③).
            await InsertAsync(
                working with
                {
                    RecordId = EpisodicCopyId(id),
                    Tier = MemoryTier.Episodic,
                    Ttl = null,
                    ConsolidatedAt = now,
                },
                ct,
                insertOrIgnore: true).ConfigureAwait(false);

            await using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE memories SET consolidated_at = $now WHERE record_id = $id AND consolidated_at IS NULL";
                cmd.Parameters.AddWithValue("$now", Iso(now));
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await LogAsync(id, "promote", $"working → episodic copy {EpisodicCopyId(id)}", ct).ConfigureAwait(false);
            promoted++;
        }

        return promoted;
    }

    /// <summary>Derived id of the durable episodic copy of a working record.</summary>
    public static string EpisodicCopyId(string recordId) => $"{recordId}#episodic";

    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // Only consolidated working records may be trimmed: an unconsolidated one would lose data.
        //
        // Retained records are excluded even though they are Working and consolidated, because retention does
        // not pin the tier — a retained Working record is a legal combination, and it has a TTL this loop would
        // otherwise act on with a physical DELETE. Retention is the one promise trim must not break.
        var ids = await QueryIdsAsync(
            "SELECT record_id FROM memories WHERE tier = 'Working' AND consolidated_at IS NOT NULL "
            + "AND scope != 'Retain' "
            + "AND ttl_expires_at IS NOT NULL AND ttl_expires_at <= $now",
            ("$now", Iso(now)),
            ct).ConfigureAwait(false);

        foreach (var id in ids)
        {
            await using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM memories WHERE record_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM memory_embeddings WHERE record_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await LogAsync(id, "trim", "ttl expired", ct).ConfigureAwait(false);
        }

        return ids.Count;
    }

    public async Task<int> CompactAsync(CancellationToken ct = default)
    {
        var ids = await QueryIdsAsync(
            "SELECT record_id FROM memories WHERE pending_compact = 1", ("", ""), ct, noParameters: true)
            .ConfigureAwait(false);

        foreach (var id in ids)
        {
            await LogAsync(id, "compact", "physical removal", ct).ConfigureAwait(false);

            await using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE record_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return ids.Count;
    }

    public async Task<int> RebuildIndexAsync(CancellationToken ct = default)
    {
        // FTS5 keeps a shadow index over the content table; 'rebuild' re-derives it from scratch. Both indexes
        // are projections of the same rows — the bodies and their bigrams — so both are rebuilt, or a repair
        // would leave the CJK half answering from whatever it held before.
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO memory_fts(memory_fts) VALUES('rebuild');"
                + "INSERT INTO memory_fts_cjk(memory_fts_cjk) VALUES('rebuild')";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var count = _db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM memories WHERE valid_to IS NULL";
        var indexed = await count.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(indexed, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ConsolidationEntry>> LogAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "SELECT id, record_id, at, action, detail FROM consolidation_log ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var entries = new List<ConsolidationEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new ConsolidationEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseTime(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return entries;
    }

    public async Task<IReadOnlyList<MemoryRevision>> RevisionsAsync(string recordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "SELECT record_id, at, before, after, actor FROM memory_revisions WHERE record_id = $id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", recordId);

        var revisions = new List<MemoryRevision>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            revisions.Add(new MemoryRevision(
                reader.GetString(0),
                ParseTime(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                Enum.Parse<Actor>(reader.GetString(4))));
        }

        return revisions;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync().ConfigureAwait(false);

        // Microsoft.Data.Sqlite pools connections; without clearing the pool the file stays locked on
        // Windows even after the connection is closed, so the workspace cannot be deleted or moved.
        SqliteConnection.ClearAllPools();
    }

    private const string SelectColumns =
        "SELECT record_id, tier, scope, body, source_event_seq, created_at, valid_from, valid_to, ttl_expires_at, "
        + "consolidated_at, confidence, importance, veracity, superseded_by, edited_by FROM memories";

    private async Task InsertAsync(
        MemoryRecord record,
        CancellationToken ct,
        SqliteTransaction? tx = null,
        bool insertOrIgnore = false,
        IReadOnlyDictionary<string, float[]?>? embedded = null)
    {
        var createdAt = record.CreatedAt == default ? _clock.GetUtcNow() : record.CreatedAt;
        var validFrom = record.ValidFrom ?? createdAt;
        var ttlExpires = record.Tier == MemoryTier.Working
            ? createdAt + (record.Ttl ?? _options.DefaultWorkingTtl)
            : (DateTimeOffset?)null;

        await using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = (insertOrIgnore ? "INSERT OR IGNORE INTO memories(" : "INSERT INTO memories(") + """
            record_id, tier, scope, body, body_cjk, source_event_seq, created_at, valid_from, valid_to,
                                 ttl_expires_at, consolidated_at, confidence, importance, veracity, superseded_by,
                                 edited_by, pending_compact, last_recalled_at)
            VALUES ($id, $tier, $scope, $body, $cjk, $seq, $created, $from, $to, $ttl, $consolidated, $confidence,
                    $importance, $veracity, $superseded, $edited, 0, $recalled)
            """;
        cmd.Parameters.AddWithValue("$id", record.RecordId);
        cmd.Parameters.AddWithValue("$tier", record.Tier.ToString());
        cmd.Parameters.AddWithValue("$scope", record.Scope.ToString());
        cmd.Parameters.AddWithValue("$body", record.Body);

        // The bigrams the CJK index reads. Written with the body, never derived at query time: the index is what
        // makes a differently-worded question cheap, and it can only be built from what is stored here.
        cmd.Parameters.AddWithValue("$cjk", CjkText.Index(record.Body));
        cmd.Parameters.AddWithValue("$seq", (object?)record.SourceEventSeq ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(createdAt));
        cmd.Parameters.AddWithValue("$from", Iso(validFrom));
        cmd.Parameters.AddWithValue("$to", record.ValidTo is null ? DBNull.Value : Iso(record.ValidTo.Value));
        cmd.Parameters.AddWithValue("$ttl", ttlExpires is null ? DBNull.Value : Iso(ttlExpires.Value));
        cmd.Parameters.AddWithValue(
            "$consolidated", record.ConsolidatedAt is null ? DBNull.Value : Iso(record.ConsolidatedAt.Value));
        cmd.Parameters.AddWithValue("$confidence", record.Confidence);
        cmd.Parameters.AddWithValue("$importance", record.Importance);
        cmd.Parameters.AddWithValue("$veracity", record.Veracity);
        cmd.Parameters.AddWithValue("$superseded", (object?)record.SupersededBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$edited", record.EditedBy is null ? DBNull.Value : record.EditedBy.Value.ToString());
        cmd.Parameters.AddWithValue(
            "$recalled", record.LastRecalledAt is null ? DBNull.Value : Iso(record.LastRecalledAt.Value));


        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (_options.Embeddings.Dimensions > 0)
        {
            var vector = embedded is not null && embedded.TryGetValue(record.RecordId, out var precomputed)
                ? precomputed
                : await TryEmbedAsync(record.Body, ct).ConfigureAwait(false);
            if (vector is not null)
            {
                await StoreEmbeddingAsync(record.RecordId, vector, ct, tx).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Moves a record to another tier, on a person's judgement.
    /// <para>
    /// Exposed because "this is a fact I rely on" and "this is something I once guessed" are judgements no
    /// scorer can make: a preference promoted to <c>Semantic</c> is one the demotion rule will then protect,
    /// and a guess left there is one it will eventually take away. Returns false for an id this store has
    /// never seen, so a caller can tell a rejected edit from a silent one.
    /// </para>
    /// </summary>
    public async Task<bool> SetTierAsync(string recordId, MemoryTier tier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE memories SET tier = $tier, edited_by = 'Human' WHERE record_id = $id";
        cmd.Parameters.AddWithValue("$tier", tier.ToString());
        cmd.Parameters.AddWithValue("$id", recordId);

        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        await LogAsync(recordId, "tier", tier.ToString(), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Marks a record as retained, or puts it back under the ordinary forgetting rules.
    /// <para>
    /// The second of the two axes a person controls. Tier says how much a record is trusted; this says whether it
    /// is allowed to be forgotten, and it is the only defence <see cref="DemoteIdleAsync"/> and
    /// <see cref="TrimAsync"/> do not have a substitute for — a fact you rely on is exactly the one the 90-day
    /// clock would otherwise take away from you.
    /// </para>
    /// <para>
    /// Deliberately does not touch the tier. Pinning Retain to a single tier would have been simpler to explain,
    /// but it would have made the Procedural tier unprotectable: a retained "how to deploy this" note would
    /// arrive as an established fact. The two axes are independent, so a retained Working record is a legal
    /// combination — and Trim excludes it for that reason.
    /// </para>
    /// <para>
    /// Returns false for an id this store has never seen, so a caller can tell a rejected edit from a silent one.
    /// </para>
    /// </summary>
    public async Task<bool> SetScopeAsync(string recordId, MemoryScope scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE memories SET scope = $scope, edited_by = 'Human' WHERE record_id = $id";
        cmd.Parameters.AddWithValue("$scope", scope.ToString());
        cmd.Parameters.AddWithValue("$id", recordId);

        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        await LogAsync(recordId, "scope", scope.ToString(), ct).ConfigureAwait(false);
        return true;
    }


    private async Task StoreEmbeddingAsync(string recordId, float[] vector, CancellationToken ct, SqliteTransaction? tx)
    {
        await using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR REPLACE INTO memory_embeddings(record_id, model_id, dimensions, vector) VALUES ($id, $model, $dims, $vec)";
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.Parameters.AddWithValue("$model", _options.Embeddings.ModelId);
        cmd.Parameters.AddWithValue("$dims", vector.Length);
        cmd.Parameters.AddWithValue("$vec", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<float[]?> TryEmbedAsync(string text, CancellationToken ct)
    {
        if (_options.Embeddings.Dimensions <= 0)
        {
            return null;
        }

        try
        {
            return await _options.Embeddings.EmbedAsync(text, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline degradation: recall falls back to lexical + FTS, records are untouched.
            return null;
        }
    }

    private async Task<Dictionary<string, float[]>> LoadEmbeddingsAsync(
        IReadOnlyList<string> recordIds,
        CancellationToken ct)
    {
        var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (recordIds.Count == 0 || _options.Embeddings.Dimensions <= 0)
        {
            return result;
        }

        await using var cmd = _db.CreateCommand();
        var names = new List<string>(recordIds.Count);
        for (var i = 0; i < recordIds.Count; i++)
        {
            var name = $"$r{i}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, recordIds[i]);
        }

        cmd.Parameters.AddWithValue("$model", _options.Embeddings.ModelId);
        cmd.CommandText =
            $"SELECT record_id, dimensions, vector FROM memory_embeddings WHERE model_id = $model AND record_id IN ({string.Join(",", names)})";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var blob = (byte[])reader.GetValue(2);
            var vector = new float[blob.Length / sizeof(float)];
            MemoryMarshal.Cast<byte, float>(blob).CopyTo(vector);
            result[reader.GetString(0)] = vector;
        }

        return result;
    }

    /// <summary>
    /// The rows a query could plausibly want: one index for Latin, another for CJK, both bm25-ranked and both
    /// bounded by the same candidate limit, then merged by record id — so a mixed query reaches a record through
    /// either half of it, and this stays a coarse filter the scorer still decides on.
    /// <para>
    /// The two indexes are not on a common scale: bm25 is computed over the corpus behind each, and the CJK
    /// index's terms are bigrams rather than words. Each index's ranks are therefore scaled against its own best
    /// hit before the union, and a record both indexes found keeps the better of the two.
    /// </para>
    /// <para>
    /// The CJK branch used to be a scan of the whole table with a LIKE per bigram, which is the cost this
    /// replaced: the same overlap, answered from an index instead of by reading every body.
    /// </para>
    /// </summary>
    private async Task<List<MemoryCandidate>> LoadCandidatesAsync(
        string? match,
        string? cjkMatch,
        CancellationToken ct)
    {
        var found = new Dictionary<string, (MemoryCandidate Candidate, double Quality)>(StringComparer.Ordinal);

        if (match is not null)
        {
            await LoadFromIndexAsync("memory_fts", match, found, ct).ConfigureAwait(false);
        }

        if (cjkMatch is not null)
        {
            await LoadFromIndexAsync("memory_fts_cjk", cjkMatch, found, ct).ConfigureAwait(false);
        }

        return found.Values
            .OrderByDescending(v => v.Quality)
            .ThenBy(v => v.Candidate.RecordId, StringComparer.Ordinal)
            .Select(v => v.Candidate)
            .ToList();
    }

    /// <summary>
    /// Runs one index's query and folds its rows into <paramref name="found"/>, keeping the better of two hits
    /// for the same record. The index name is one of the two constants in <see cref="LoadCandidatesAsync"/>,
    /// never user text.
    /// </summary>
    private async Task LoadFromIndexAsync(
        string index,
        string match,
        Dictionary<string, (MemoryCandidate Candidate, double Quality)> found,
        CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
          SELECT m.record_id, m.body, m.tier, m.scope, m.created_at, m.importance, m.veracity, m.confidence,
                 bm25({index}) AS rank, m.source_event_seq
          FROM {index}
          JOIN memories m ON m.rowid = {index}.rowid
          WHERE {index} MATCH $q AND m.valid_to IS NULL
          ORDER BY rank
          LIMIT $candidates
          """;
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$candidates", _options.CandidateLimit);

        var rows = new List<(MemoryCandidate Candidate, double Magnitude)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // bm25 is negative and lower is better, so the magnitude is the strength of the match.
                rows.Add((ReadCandidate(reader), Math.Abs(reader.GetDouble(8))));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        var best = rows.Max(r => r.Magnitude);
        foreach (var (candidate, magnitude) in rows)
        {
            var quality = best > 0.0 ? magnitude / best : 0.0;
            if (!found.TryGetValue(candidate.RecordId, out var existing) || quality > existing.Quality)
            {
                found[candidate.RecordId] = (candidate with { FtsRank = -quality }, quality);
            }
        }
    }

    private MemoryCandidate ReadCandidate(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Enum.Parse<MemoryTier>(reader.GetString(2)),
        ParseScope(reader.GetString(3)),
        ParseTime(reader.GetString(4)),
        reader.GetDouble(5),
        reader.GetDouble(6),
        reader.GetDouble(7),
        FtsRank: null,
        SourceEventSeq: reader.IsDBNull(9) ? null : reader.GetInt64(9));

    /// <summary>
    /// Reads a stored scope, falling back to <see cref="MemoryScope.Recall"/> for a value this build no longer
    /// has.
    /// <para>
    /// The column stores the enum name as text, so shrinking the enum can leave rows behind — the doc's
    /// <c>Global</c> is the one to worry about. Parsing strictly would throw on the first read and take the whole
    /// memory view down over one row, and guessing the other way (treating an unknown name as Retain) would hand a
    /// record a protection nobody asked for. Falling back to the ordinary behaviour is the only option that cannot
    /// surprise anybody.
    /// </para>
    /// <para>
    /// The fallback is quiet, which is not the same as unnoticed: the value is counted here and named once on
    /// stderr, because a store whose rows all carry a scope this build does not know behaves like a store where
    /// everything was silently let go, and nothing else in the system would say so.
    /// </para>
    /// </summary>
    private MemoryScope ParseScope(string value)
    {
        if (Enum.TryParse<MemoryScope>(value, ignoreCase: true, out var scope))
        {
            return scope;
        }

        if (_unknownScopes.TryGetValue(value, out var seen))
        {
            _unknownScopes[value] = seen + 1;
        }
        else
        {
            _unknownScopes[value] = 1;
            Console.Error.WriteLine(
                $"memory: scope '{value}' is not one this build has — read as {MemoryScope.Recall}; "
                + "rows carrying it behave as ordinary recall");
        }

        return MemoryScope.Recall;
    }

    private readonly Dictionary<string, int> _unknownScopes = new(StringComparer.Ordinal);

    /// <summary>
    /// Scope names read back that this build does not have, and how many times a row carrying each was read.
    /// Reported per value on stderr the first time; kept here so a caller can see the drift without reading the
    /// log.
    /// </summary>
    internal IReadOnlyDictionary<string, int> UnknownScopes => _unknownScopes;

    private async Task<List<string>> QueryIdsAsync(
        string sql,
        (string Name, string Value) parameter,
        CancellationToken ct,
        bool noParameters = false)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        if (!noParameters)
        {
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task LogAsync(string recordId, string action, string? detail, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "INSERT INTO consolidation_log(record_id, at, action, detail) VALUES ($id, $at, $action, $detail)";
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.Parameters.AddWithValue("$at", Iso(_clock.GetUtcNow()));
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private MemoryRecord ReadRecord(SqliteDataReader reader) => new()
    {
        RecordId = reader.GetString(0),
        Tier = Enum.Parse<MemoryTier>(reader.GetString(1)),
        Scope = ParseScope(reader.GetString(2)),
        Body = reader.GetString(3),
        SourceEventSeq = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        CreatedAt = ParseTime(reader.GetString(5)),
        ValidFrom = ParseTime(reader.GetString(6)),
        ValidTo = reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
        ConsolidatedAt = reader.IsDBNull(9) ? null : ParseTime(reader.GetString(9)),
        Confidence = reader.GetDouble(10),
        Importance = reader.GetDouble(11),
        Veracity = reader.GetDouble(12),
        SupersededBy = reader.IsDBNull(13) ? null : reader.GetString(13),
        EditedBy = reader.IsDBNull(14) ? null : Enum.Parse<Actor>(reader.GetString(14)),
    };

    /// <summary>
    /// Turns free text into a safe FTS5 MATCH expression: quoted tokens joined by OR, or null when the query
    /// has no usable token — so a syntax error is never built out of user input.
    /// <para>
    /// Latin runs are split on non-alphanumerics. Chinese has no spaces, so a whole sentence would become one
    /// token and match nothing but itself: "我不太喜欢被打断" would not find a record reading
    /// "用户不喜欢被反复确认". A run that is entirely CJK is therefore cut into character bigrams, which is
    /// the cheapest thing that makes the two overlap. A real segmenter is better and is a separate decision,
    /// but without this the recall path is dead for the language this project is written in.
    /// </para>
    /// <para>
    /// This is only the Latin half of a query. A mixed query also gets a CJK expression from
    /// <see cref="CjkText.MatchExpression"/>, and the store unions the two — a CJK run reaching here is a
    /// leftover the bigram index cannot serve (a single character), not the only way the query is searched.
    /// </para>
    /// </summary>
    internal static string? BuildMatchExpression(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        var expanded = tokens.SelectMany(SplitRun).Where(t => t.Length >= 2).ToList();
        var usable = expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        return string.Join(" OR ", usable.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }

    /// <summary>
    /// Splits one run into searchable pieces: a CJK run into bigrams, anything else left whole.
    /// <para>
    /// A single CJK character is kept as itself, because it is a word often enough (「钱」「好」) and dropping it
    /// would throw away the shortest queries. Kept for the <c>internal</c> expression builder's own sake: the
    /// bigram index (<see cref="CjkText"/>) covers runs of two or more, so this is what is left for the Latin
    /// index to find.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SplitRun(string run)
    {
        if (run.Length < 2 || !run.All(CjkText.IsCjk))
        {
            return new[] { run };
        }

        if (run.Length == 2)
        {
            return new[] { run };
        }

        var pieces = new List<string>(run.Length - 1);
        for (var i = 0; i + 1 < run.Length; i++)
        {
            pieces.Add(run.Substring(i, 2));
        }

        return pieces;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
