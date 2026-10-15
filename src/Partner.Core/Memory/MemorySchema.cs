using Microsoft.Data.Sqlite;

namespace Partner.Core.Memory;

/// <summary>
/// SQLite schema for the memory store.
/// <para>
/// Deviation from design doc §2.5.6 ①, recorded deliberately: the doc lists one table per tier, but FTS5
/// external-content tables can only mirror <b>one</b> content table. All tiers therefore live in
/// <c>memories</c> with a <c>tier</c> column (identical model, one index, no duplicated bodies), and the
/// optional knowledge-graph table is not implemented yet.
/// </para>
/// </summary>
internal static class MemorySchema
{
    /// <summary>
    /// Everything that can be created unconditionally, in one batch. The CJK triggers are <b>not</b> in here:
    /// they name <c>body_cjk</c>, and a store old enough to lack that column fails at <c>CREATE TRIGGER</c> —
    /// SQLite resolves the columns of a trigger body when the statement is prepared, not when it fires. They are
    /// created by the store after the migrations, which is the only place that can promise the column exists.
    /// </summary>
    public const string Ddl = Tables + CjkIndexTable;

    private const string Tables = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = FULL;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS memories (
            rowid            INTEGER PRIMARY KEY,
            record_id        TEXT    NOT NULL UNIQUE,
            tier             TEXT    NOT NULL,
            scope            TEXT    NOT NULL,
            body             TEXT    NOT NULL,
            body_cjk         TEXT    NOT NULL DEFAULT '',
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
            pending_compact  INTEGER NOT NULL DEFAULT 0,
            last_recalled_at TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_memories_tier        ON memories(tier);
        CREATE INDEX IF NOT EXISTS ix_memories_source      ON memories(source_event_seq);
        CREATE INDEX IF NOT EXISTS ix_memories_scope       ON memories(scope);
        CREATE INDEX IF NOT EXISTS ix_memories_created_at  ON memories(created_at);
        CREATE INDEX IF NOT EXISTS ix_memories_importance  ON memories(importance);
        CREATE INDEX IF NOT EXISTS ix_memories_valid_to    ON memories(valid_to);
        CREATE INDEX IF NOT EXISTS ix_memories_consolidated ON memories(consolidated_at);
        CREATE INDEX IF NOT EXISTS ix_memories_ttl         ON memories(ttl_expires_at);

        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
            body,
            content='memories',
            content_rowid='rowid',
            tokenize='unicode61'
        );

        CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
            INSERT INTO memory_fts(rowid, body) VALUES (new.rowid, new.body);
        END;

        CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
            INSERT INTO memory_fts(memory_fts, rowid, body) VALUES ('delete', old.rowid, old.body);
        END;

        CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
            INSERT INTO memory_fts(memory_fts, rowid, body) VALUES ('delete', old.rowid, old.body);
            INSERT INTO memory_fts(rowid, body) VALUES (new.rowid, new.body);
        END;

        CREATE TABLE IF NOT EXISTS memory_embeddings (
            record_id  TEXT    NOT NULL,
            model_id   TEXT    NOT NULL,
            dimensions INTEGER NOT NULL,
            vector     BLOB    NOT NULL,
            PRIMARY KEY (record_id, model_id)
        );

        CREATE TABLE IF NOT EXISTS memory_revisions (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            record_id TEXT NOT NULL,
            at        TEXT NOT NULL,
            before    TEXT NOT NULL,
            after     TEXT NOT NULL,
            actor     TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_revisions_record ON memory_revisions(record_id);

        CREATE TABLE IF NOT EXISTS consolidation_log (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            record_id TEXT NOT NULL,
            at        TEXT NOT NULL,
            action    TEXT NOT NULL,
            detail    TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_consolidation_action ON consolidation_log(action);
        """;

    /// <summary>
    /// The second index, over the bigrams in <c>body_cjk</c> (see <see cref="CjkText"/>).
    /// <para>
    /// Same shape as <c>memory_fts</c> — external content over the same rows, kept in step by the same three
    /// triggers — but a separate table because it indexes a different column of the same row. The table is here
    /// and the triggers are <see cref="CjkTriggers"/>, created by the store only once the migrations have
    /// established the column they name.
    /// </para>
    /// </summary>
    private const string CjkIndexTable = """
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts_cjk USING fts5(
            body_cjk,
            content='memories',
            content_rowid='rowid',
            tokenize='unicode61'
        );
        """;

    /// <summary>
    /// The three triggers that keep <c>memory_fts_cjk</c> in step with <c>memories</c>.
    /// <para>
    /// Run on their own, after the migrations, for the reason in <see cref="Ddl"/> — and because they must not
    /// fire during the v1→v2 backfill: an UPDATE that fires them against a row the index has never seen makes
    /// FTS5 report the database as corrupt (measured), so the column is filled and the index rebuilt first, and
    /// only then are the triggers put in place.
    /// </para>
    /// </summary>
    internal const string CjkTriggers = """
        CREATE TRIGGER IF NOT EXISTS memories_ai_cjk AFTER INSERT ON memories BEGIN
            INSERT INTO memory_fts_cjk(rowid, body_cjk) VALUES (new.rowid, new.body_cjk);
        END;

        CREATE TRIGGER IF NOT EXISTS memories_ad_cjk AFTER DELETE ON memories BEGIN
            INSERT INTO memory_fts_cjk(memory_fts_cjk, rowid, body_cjk) VALUES ('delete', old.rowid, old.body_cjk);
        END;

        CREATE TRIGGER IF NOT EXISTS memories_au_cjk AFTER UPDATE ON memories BEGIN
            INSERT INTO memory_fts_cjk(memory_fts_cjk, rowid, body_cjk) VALUES ('delete', old.rowid, old.body_cjk);
            INSERT INTO memory_fts_cjk(rowid, body_cjk) VALUES (new.rowid, new.body_cjk);
        END;
        """;

    /// <summary>
    /// The version this build writes into <c>PRAGMA user_version</c>. A store carrying a higher one was written
    /// by a newer build and is refused rather than half-read.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// The schema's history, one step per version, lowest first.
    /// <para>
    /// `CREATE TABLE IF NOT EXISTS` silently leaves an existing table alone, so a new column reaches new
    /// workspaces and never old ones. Before this list existed, each statement was run on every open and its
    /// failure swallowed, which meant a future step that changed a column type or split a table had no way to
    /// tell "already done" from "did not work" — it would fail quietly and leave a store that opens fine and
    /// then misbehaves.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SchemaMigration> Migrations { get; } = new[]
    {
        new SchemaMigration(
            1,
            "last_recalled_at — the demotion clock, absent from stores created before the column existed",
            new[] { "ALTER TABLE memories ADD COLUMN last_recalled_at TEXT" },
            TolerateFailures: true),
        new SchemaMigration(
            2,
            "body_cjk — the bigram index the CJK coarse filter reads, backfilled row by row",
            new[] { "ALTER TABLE memories ADD COLUMN body_cjk TEXT NOT NULL DEFAULT ''" },
            TolerateFailures: true,
            Data: BackfillCjk),
    };

    /// <summary>
    /// Fills <c>body_cjk</c> for every row that predates the column, then rebuilds the index over it.
    /// <para>
    /// The one step SQL cannot express: a bigram is a property of the text, and SQLite has no way to cut a string
    /// into overlapping pairs — so the values are computed in C# and written row by row. The index is then derived
    /// from the finished column by its own rebuild command rather than by replaying triggers: nothing here is
    /// indexed yet, and the triggers that would normally do it are not created until after this runs.
    /// </para>
    /// <para>
    /// Runs in one transaction: a half-backfilled store would answer some queries and not others.
    /// </para>
    /// </summary>
    private static void BackfillCjk(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        var rows = new List<(long RowId, string Body)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT rowid, body FROM memories WHERE body <> ''";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = "UPDATE memories SET body_cjk = $cjk WHERE rowid = $rowid";
            var cjk = write.Parameters.Add("$cjk", SqliteType.Text);
            var rowid = write.Parameters.Add("$rowid", SqliteType.Integer);

            foreach (var (id, body) in rows)
            {
                cjk.Value = CjkText.Index(body);
                rowid.Value = id;
                write.ExecuteNonQuery();
            }
        }

        using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = "INSERT INTO memory_fts_cjk(memory_fts_cjk) VALUES('rebuild')";
            rebuild.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}

/// <summary>One step of the schema's history: what a store at <c>To - 1</c> runs to reach <c>To</c>.</summary>
/// <param name="To">Version this step produces. Steps run in ascending order, each followed by a version stamp.</param>
/// <param name="Description">What the step does, so a failure names the thing that failed.</param>
/// <param name="Statements">SQL to execute, in order.</param>
/// <param name="TolerateFailures">
/// True only where a statement's failure <b>is</b> the expected outcome: SQLite has no
/// <c>ADD COLUMN IF NOT EXISTS</c>, so re-running a column addition fails with "duplicate column" and that
/// failure means there was nothing to do. Every other step fails loudly — a migration that silently did not run
/// is worse than one that refuses to open the store.
/// </param>
/// <param name="Data">
/// For work SQL cannot do (see <see cref="BackfillCjk"/>): runs after <paramref name="Statements"/>, in its own
/// transaction. A step with a delegate is not a step whose failure may be tolerated — the flag covers the
/// statements only, so a half-written backfill cannot pass for success.
/// </param>
internal sealed record SchemaMigration(
    int To,
    string Description,
    IReadOnlyList<string> Statements,
    bool TolerateFailures = false,
    Action<SqliteConnection>? Data = null);
