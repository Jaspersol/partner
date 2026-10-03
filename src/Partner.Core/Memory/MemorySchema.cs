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
    public const string Ddl = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = FULL;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS memories (
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
            pending_compact  INTEGER NOT NULL DEFAULT 0,
            last_recalled_at TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_memories_tier        ON memories(tier);
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
    /// One-off column additions for stores created before the column existed.
    /// <para>
    /// `CREATE TABLE IF NOT EXISTS` silently leaves an existing table alone, so a new column reaches new
    /// workspaces and never old ones. Each statement therefore has to be run separately and tolerate failing —
    /// SQLite has no `ADD COLUMN IF NOT EXISTS`, and the failure of "duplicate column" is the answer we want.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Migrations { get; } = new[]
    {
        "ALTER TABLE memories ADD COLUMN last_recalled_at TEXT",
    };
}
