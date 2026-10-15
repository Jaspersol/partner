using System.Globalization;
using Microsoft.Data.Sqlite;
using Partner.Core.Memory;
using Partner.Core.Tests.Sessions;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// Schema versioning (todo M2.1): an old workspace still opens, a new one is stamped at the current version, and a
/// workspace from the future is refused instead of half-read.
/// </summary>
public sealed class MemorySchemaMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    private string DbPath => Path.Combine(_dir, "memory", "memory.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// The <c>memories</c> table exactly as it was before <c>last_recalled_at</c> existed. Everything else the old
    /// build had — the FTS table, its triggers, the indexes — is created from the real DDL below, because a store
    /// missing those is not an old store, it is a different one.
    /// </summary>
    private const string OldShape = """
        CREATE TABLE memories (
            rowid            INTEGER PRIMARY KEY,
            record_id        TEXT    NOT NULL UNIQUE,
            tier             TEXT    NOT NULL,
            scope            TEXT    NOT NULL,
            body             TEXT    NOT NULL,
            source_event_seq INTEGER,
            created_at       TEXT    NOT NULL,
            valid_from       TEXT    NOT NULL,
            valid_to         TEXT,
            ttl_expires_at   TEXT,
            consolidated_at  TEXT,
            confidence       REAL    NOT NULL,
            importance       REAL    NOT NULL,
            veracity         REAL    NOT NULL,
            superseded_by    TEXT,
            edited_by        TEXT,
            pending_compact  INTEGER NOT NULL DEFAULT 0
        );
        """;

    private void CreateOldStore(string? extra = null, string? row = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var db = new SqliteConnection($"Data Source={DbPath}");
        db.Open();
        Execute(db, OldShape);

        // The rest of the old build's schema. `IF NOT EXISTS` leaves the table above alone and creates the FTS
        // index and its triggers — which matters for the row below: an UPDATE on a row the index never saw makes
        // FTS5 report the database as corrupt, so the fixture has to put its row in the way a real store did.
        Execute(db, MemorySchema.Ddl);

        if (extra is not null)
        {
            Execute(db, extra);
        }

        if (row is not null)
        {
            Execute(db, row);
        }
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Query<T>(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    private static bool HasColumn(string dbPath, string table, string column)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static int UserVersion(string dbPath)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        return Query<int>(db, "PRAGMA user_version");
    }

    [Fact]
    public async Task A_store_from_before_the_column_existed_opens_and_gains_it()
    {
        CreateOldStore(row:
            "INSERT INTO memories(record_id, tier, scope, body, created_at, valid_from, confidence, importance, "
            + "veracity, pending_compact) VALUES ('old-1', 'Semantic', 'Recall', '十年前的记录', "
            + "'2026-09-01T00:00:00.0000000+00:00', '2026-09-01T00:00:00.0000000+00:00', 0.5, 0.5, 0.5, 0)");

        await using (var store = new MemoryStore(DbPath, null, _clock))
        {
            Assert.Equal(MemorySchema.Version, store.SchemaVersion);
            Assert.Equal(MemorySchema.Version, UserVersion(DbPath));
            Assert.True(HasColumn(DbPath, "memories", "last_recalled_at"), "the column the demotion clock reads");

            // The record that was already there is still there — a migration that loses data is not a migration.
            var kept = await store.ListAsync();
            Assert.Equal("old-1", Assert.Single(kept).RecordId);

            // The demotion scan is the one query that reads the migrated column, so it is also the proof that the
            // column is not merely present but usable: a record never recalled is idle, and idleness is measured
            // through last_recalled_at. Run before anything else touches the store, so the count is unambiguous.
            Assert.Equal(1, await store.DemoteIdleAsync());
            Assert.Equal(MemoryTier.Episodic, (await store.GetAsync("old-1"))!.Tier);

            // And the store works: writes and the recall path.
            await store.WriteAsync(new MemoryRecord
            {
                RecordId = "new-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Recall,
                Body = "deployment pipelines in region 7",
            });
            Assert.Single(await store.RecallAsync("deployment pipelines"));
        }
    }

    [Fact]
    public async Task An_old_store_backfills_the_bigrams_its_rows_were_never_indexed_with()
    {
        // A store from before the bigram index has bodies but no body_cjk and nothing in memory_fts_cjk, so a
        // Chinese question worded differently from the record is answered with nothing — the failure is silent,
        // which is why the migration has to be proven against real content rather than against a column name.
        CreateOldStore(row:
            "INSERT INTO memories(record_id, tier, scope, body, created_at, valid_from, confidence, importance, "
            + "veracity, pending_compact) VALUES ('old-1', 'Semantic', 'Recall', '用户不喜欢被反复确认', "
            + "'2026-09-01T00:00:00.0000000+00:00', '2026-09-01T00:00:00.0000000+00:00', 0.5, 0.5, 0.5, 0)");

        await using var store = new MemoryStore(DbPath, null, _clock);

        Assert.Contains(await store.RecallAsync("我不太喜欢被打断"), h => h.RecordId == "old-1");

        // The triggers are created after the backfill, so a record written now is indexed the ordinary way.
        await store.WriteAsync(new MemoryRecord
        {
            RecordId = "new-1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "用户希望先看结论",
        });
        Assert.Contains(await store.RecallAsync("希望先看结论"), h => h.RecordId == "new-1");
    }

    [Fact]
    public async Task A_store_that_already_has_the_column_still_opens()
    {
        // What the old swallowed-ALTER code left behind: the column is there, the version was never recorded. The
        // step re-runs, SQLite refuses the duplicate, and that refusal is the expected outcome for this step only.
        CreateOldStore(extra: "ALTER TABLE memories ADD COLUMN last_recalled_at TEXT");

        await using var store = new MemoryStore(DbPath, null, _clock);

        Assert.Equal(MemorySchema.Version, store.SchemaVersion);
        Assert.Equal(MemorySchema.Version, UserVersion(DbPath));
    }

    [Fact]
    public async Task A_new_store_is_born_at_the_current_version()
    {
        await using var store = new MemoryStore(DbPath, null, _clock);

        Assert.Equal(MemorySchema.Version, store.SchemaVersion);
        Assert.Equal(MemorySchema.Version, UserVersion(DbPath));
        Assert.True(HasColumn(DbPath, "memories", "last_recalled_at"));
    }

    [Fact]
    public async Task Reopening_a_migrated_store_does_not_migrate_it_again()
    {
        CreateOldStore();

        await using (var first = new MemoryStore(DbPath, null, _clock))
        {
            Assert.Equal(MemorySchema.Version, first.SchemaVersion);
        }

        // The stamp is what makes the second open a no-op rather than a second ALTER.
        await using var second = new MemoryStore(DbPath, null, _clock);
        Assert.Equal(MemorySchema.Version, second.SchemaVersion);
        Assert.Equal(MemorySchema.Version, UserVersion(DbPath));
    }

    [Fact]
    public async Task A_store_written_by_a_newer_build_is_refused()
    {
        await using (var store = new MemoryStore(DbPath, null, _clock))
        {
            Assert.Equal(MemorySchema.Version, store.SchemaVersion);
        }

        using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            db.Open();
            Execute(db, $"PRAGMA user_version = {MemorySchema.Version + 1}");
        }

        // Reading it would mean guessing at a schema this build has never seen, and writing to it could drop
        // columns the newer build added. Refusing is the only answer that cannot corrupt the workspace.
        var refused = Assert.Throws<InvalidOperationException>(() => new MemoryStore(DbPath, null, _clock));
        Assert.Contains("newer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_migration_step_leads_to_a_version_this_build_knows()
    {
        // A step numbered past the current version would run and then be refused by the guard above on the next
        // open — the store would be migrated into a state it calls newer than itself.
        Assert.All(MemorySchema.Migrations, step => Assert.True(
            step.To <= MemorySchema.Version,
            $"step {step.To} ({step.Description}) is past the current version {MemorySchema.Version}"));
        Assert.All(MemorySchema.Migrations, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
        Assert.All(MemorySchema.Migrations, step => Assert.NotEmpty(step.Statements));
    }
}
