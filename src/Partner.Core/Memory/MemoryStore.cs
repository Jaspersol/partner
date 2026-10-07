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
        Execute(MemorySchema.Ddl);

        // A column added after a store was created does not reach it through the DDL above — IF NOT EXISTS
        // leaves the existing table alone — so each migration is attempted and its "already there" failure
        // ignored. The alternative is a workspace that opens fine and then misbehaves on a column nobody
        // remembered to add.
        foreach (var migration in MemorySchema.Migrations)
        {
            try
            {
                Execute(migration);
            }
            catch (SqliteException)
            {
                // Duplicate column, or the table does not exist yet for some other reason. Either way the DDL
                // above already established what exists; a column that is already there is not a failure.
            }
        }
    }
    public async Task<string> WriteAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Design doc §2.5.6 ③: every write path consolidates eligible working records before committing.
        await PromoteEligibleAsync(ct).ConfigureAwait(false);

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
        // tokenizer. Whichever this body can use, the decision is still made by word overlap below.
        var cjk = ContainsCjk(incoming.Body);
        var match = cjk ? null : BuildMatchExpression(incoming.Body);
        var like = cjk ? BuildLikePattern(incoming.Body) : null;

        var candidates = await LoadCandidatesAsync(match, like, ct).ConfigureAwait(false);
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
    /// Marks what was just handed out as used, and demotes what has gone quiet.
    /// <para>
    /// Both happen on the recall path because that is the only place "used" is observable, and demotion needs a
    /// moment when the store is already open. A record nobody found for three months is not something to keep
    /// handing to a model at full trust.
    /// </para>
    /// </summary>
    private async Task MaintainAsync(IReadOnlyList<string> recalled, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        foreach (var recordId in recalled)
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE memories SET last_recalled_at = $now WHERE record_id = $id";
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$id", recordId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await DemoteIdleAsync(now, ct).ConfigureAwait(false);
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
    private async Task DemoteIdleAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - MemoryLifecycle.IdleBeforeDemotion;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "UPDATE memories SET tier = CASE tier WHEN 'Semantic' THEN 'Episodic' WHEN 'Procedural' THEN 'Episodic' ELSE 'Working' END "
            + "WHERE valid_to IS NULL AND tier != 'Working' AND scope != 'Retain' "
            + "AND (last_recalled_at IS NULL OR last_recalled_at <= $cutoff)";
        cmd.Parameters.AddWithValue("$cutoff", Iso(cutoff));

        var demoted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

        await PromoteEligibleAsync(ct).ConfigureAwait(false);

        await using var tx = _db.BeginTransaction();
        foreach (var record in list)
        {
            await InsertAsync(record, ct, tx).ConfigureAwait(false);
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

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

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

    public async Task<IReadOnlyList<MemoryHit>> RecallAsync(string query, int? limit = null, CancellationToken ct = default)
    {
        var take = Math.Max(1, limit ?? _options.RecallLimit);

        // Two coarse filters, because the index cannot serve Chinese. unicode61 treats a whole CJK sentence as
        // one token, so an FTS match for a differently-worded question finds nothing — and the tokenizer cannot
        // be swapped underneath existing stores (there is no schema versioning). So a CJK query is matched
        // against its own bigrams with OR: 我不太喜欢被打断 and 用户不喜欢被反复确认 share 喜欢/不被打,
        // and that overlap is enough to bring the record in. The scorer still decides relevance; this is only
        // a coarse filter.
        var cjk = ContainsCjk(query);
        var match = cjk ? null : BuildMatchExpression(query);
        var like = cjk ? BuildLikePattern(query) : null;

        if (match is null && like is null)
        {
            return Array.Empty<MemoryHit>();
        }

        var candidates = await LoadCandidatesAsync(match, like, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return Array.Empty<MemoryHit>();
        }
        var embeddings = await LoadEmbeddingsAsync(candidates.Select(c => c.RecordId).ToList(), ct)
            .ConfigureAwait(false);
        var queryEmbedding = await TryEmbedAsync(query, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        var result = candidates
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
                    now);
            })
            .OrderByDescending(h => h.Score.Total)
            .ThenBy(h => h.RecordId, StringComparer.Ordinal)
            .Take(take)
            .ToList();

        // What was actually handed out is what counts as used — not what merely became a candidate.
        await MaintainAsync(result.Select(hit => hit.RecordId).ToList(), ct).ConfigureAwait(false);
        return result;
    }

    public async Task<InjectionResult> InjectAsync(string query, CancellationToken ct = default)
    {
        var hits = await RecallAsync(query, _options.RecallLimit, ct).ConfigureAwait(false);

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
            cmd.CommandText = "UPDATE memories SET body = $body, edited_by = $actor WHERE record_id = $id";
            cmd.Parameters.AddWithValue("$body", newBody);
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

        // Step 1+3: soft invalidate and purge the text in one statement (the FTS trigger drops the old body).
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE memories SET valid_to = $now, body = '', pending_compact = 1, edited_by = $actor WHERE record_id = $id";
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
        // FTS5 keeps a shadow index over the content table; 'rebuild' re-derives it from scratch.
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO memory_fts(memory_fts) VALUES('rebuild')";
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

    private async Task InsertAsync(MemoryRecord record, CancellationToken ct, SqliteTransaction? tx = null, bool insertOrIgnore = false)
    {
        var createdAt = record.CreatedAt == default ? _clock.GetUtcNow() : record.CreatedAt;
        var validFrom = record.ValidFrom ?? createdAt;
        var ttlExpires = record.Tier == MemoryTier.Working
            ? createdAt + (record.Ttl ?? _options.DefaultWorkingTtl)
            : (DateTimeOffset?)null;

        await using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = (insertOrIgnore ? "INSERT OR IGNORE INTO memories(" : "INSERT INTO memories(") + """
            record_id, tier, scope, body, source_event_seq, created_at, valid_from, valid_to,
                                 ttl_expires_at, consolidated_at, confidence, importance, veracity, superseded_by,
                                 edited_by, pending_compact, last_recalled_at)
            VALUES ($id, $tier, $scope, $body, $seq, $created, $from, $to, $ttl, $consolidated, $confidence,
                    $importance, $veracity, $superseded, $edited, 0, $recalled)
            """;
        cmd.Parameters.AddWithValue("$id", record.RecordId);
        cmd.Parameters.AddWithValue("$tier", record.Tier.ToString());
        cmd.Parameters.AddWithValue("$scope", record.Scope.ToString());
        cmd.Parameters.AddWithValue("$body", record.Body);
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
            var vector = await TryEmbedAsync(record.Body, ct).ConfigureAwait(false);
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
    /// The rows a query could plausibly want.
    /// <para>
    /// FTS when the query is Latin — bm25-ranked and fast. A character scan when it is CJK, because the index
    /// holds whole sentences there and would match only the exact one. Both paths are bounded by the same
    /// candidate limit, so this stays a coarse filter and the scorer still decides what is actually relevant.
    /// </para>
    /// </summary>
    private async Task<List<MemoryCandidate>> LoadCandidatesAsync(
        string? match,
        IReadOnlyList<string>? bigrams,
        CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();

        if (match is not null)
        {
            cmd.CommandText = """
              SELECT m.record_id, m.body, m.tier, m.scope, m.created_at, m.importance, m.veracity, m.confidence,
                     bm25(memory_fts) AS rank
              FROM memory_fts
              JOIN memories m ON m.rowid = memory_fts.rowid
              WHERE memory_fts MATCH $q AND m.valid_to IS NULL
              ORDER BY rank
              LIMIT $candidates
              """;
            cmd.Parameters.AddWithValue("$q", match);
        }
        else
        {
            // One parameter per bigram, OR-ed together. Every user-supplied string is still a bound parameter —
            // the clause shape is fixed, only the count varies.
            var clause = string.Join(" OR ", Enumerable.Range(0, bigrams!.Count).Select(i => $"body LIKE $b{i} ESCAPE '\\'"));

            cmd.CommandText = $"""
              SELECT record_id, body, tier, scope, created_at, importance, veracity, confidence,
                     0.0 AS rank
              FROM memories
              WHERE valid_to IS NULL AND ({clause})
              ORDER BY importance DESC, created_at DESC
              LIMIT $candidates
              """;

            for (var i = 0; i < bigrams.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$b{i}", "%" + Escape(bigrams[i]) + "%");
            }
        }

        cmd.Parameters.AddWithValue("$candidates", _options.CandidateLimit);

        var candidates = new List<MemoryCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            candidates.Add(new MemoryCandidate(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<MemoryTier>(reader.GetString(2)),
                ParseScope(reader.GetString(3)),
                ParseTime(reader.GetString(4)),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8)));
        }

        return candidates;
    }

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
    /// </summary>
    private static MemoryScope ParseScope(string value) =>
        Enum.TryParse<MemoryScope>(value, ignoreCase: true, out var scope) ? scope : MemoryScope.Recall;

    /// <summary>
    /// Escapes the LIKE wildcards, so a query containing <c>%</c> or <c>_</c> searches for them instead of
    /// turning into a match-everything pattern.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Whether the text contains a CJK character, and so cannot be served by the index.</summary>
    private static bool ContainsCjk(string text) => text.Any(IsCjk);

    /// <summary>
    /// The query's bigrams, each of which becomes one OR-ed LIKE.
    /// <para>
    /// All of them, not the most frequent: 我不太喜欢被打断 and 用户不喜欢被反复确认 share no substring, and
    /// the bigrams they have in common are 喜欢 and 欢被. Picking by frequency picks 不太 — the negation, which
    /// appears in neither the record nor anything it is about. Every bigram is tried and the scorer decides.
    /// </para>
    /// <para>
    /// Each becomes a bound parameter, not an interpolated fragment: the clause's shape is fixed and only the
    /// parameter count varies, so no user text reaches the SQL text itself.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string>? BuildLikePattern(string query)
    {
        var tokens = Bigrams(query).Distinct(StringComparer.Ordinal).ToList();
        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>Overlapping character pairs — what lets two differently-worded sentences intersect at all.</summary>
    private static IEnumerable<string> Bigrams(string text)
    {
        var run = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                run.Append(ch);
                continue;
            }

            if (run.Length >= 2)
            {
                for (var i = 0; i + 1 < run.Length; i++)
                {
                    yield return run.ToString(i, 2);
                }
            }

            run.Clear();
        }

        if (run.Length >= 2)
        {
            for (var i = 0; i + 1 < run.Length; i++)
            {
                yield return run.ToString(i, 2);
            }
        }
    }

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

    private static MemoryRecord ReadRecord(SqliteDataReader reader) => new()
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
    /// would throw away the shortest queries.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SplitRun(string run)
    {
        if (run.Length < 2 || !run.All(IsCjk))
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

    private static bool IsCjk(char ch) =>
        ch is >= '一' and <= '鿿' || ch is >= '぀' and <= 'ヿ';

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
