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
    }

    public async Task<string> WriteAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Design doc §2.5.6 ③: every write path consolidates eligible working records before committing.
        await PromoteEligibleAsync(ct).ConfigureAwait(false);

        await InsertAsync(record, ct).ConfigureAwait(false);
        await LogAsync(record.RecordId, "write", record.Tier.ToString(), ct).ConfigureAwait(false);
        return record.RecordId;
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
        var match = BuildMatchExpression(query);
        if (match is null)
        {
            return Array.Empty<MemoryHit>();
        }

        var candidates = await LoadCandidatesAsync(match, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return Array.Empty<MemoryHit>();
        }

        var embeddings = await LoadEmbeddingsAsync(candidates.Select(c => c.RecordId).ToList(), ct)
            .ConfigureAwait(false);
        var queryEmbedding = await TryEmbedAsync(query, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        return candidates
            .Select(candidate =>
            {
                embeddings.TryGetValue(candidate.RecordId, out var candidateEmbedding);
                var score = _options.Scorer.Score(candidate, query, queryEmbedding, candidateEmbedding, now);
                return new MemoryHit(candidate.RecordId, candidate.Body, score, _options.Scorer.Explain(score));
            })
            .OrderByDescending(h => h.Score.Total)
            .ThenBy(h => h.RecordId, StringComparer.Ordinal)
            .Take(take)
            .ToList();
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
        var ids = await QueryIdsAsync(
            "SELECT record_id FROM memories WHERE tier = 'Working' AND consolidated_at IS NOT NULL "
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
                                 edited_by, pending_compact)
            VALUES ($id, $tier, $scope, $body, $seq, $created, $from, $to, $ttl, $consolidated, $confidence,
                    $importance, $veracity, $superseded, $edited, 0)
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

    private async Task<List<MemoryCandidate>> LoadCandidatesAsync(string match, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
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
        cmd.Parameters.AddWithValue("$candidates", _options.CandidateLimit);

        var candidates = new List<MemoryCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            candidates.Add(new MemoryCandidate(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<MemoryTier>(reader.GetString(2)),
                Enum.Parse<MemoryScope>(reader.GetString(3)),
                ParseTime(reader.GetString(4)),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8)));
        }

        return candidates;
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
        Scope = Enum.Parse<MemoryScope>(reader.GetString(2)),
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
    /// Turns free text into a safe FTS5 MATCH expression: quoted tokens joined by OR. Returns null when the
    /// query has no usable token (never builds a syntax-erroring expression out of user input).
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

        var usable = tokens.Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        return string.Join(" OR ", usable.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
