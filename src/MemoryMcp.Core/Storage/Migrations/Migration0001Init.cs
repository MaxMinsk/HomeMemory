using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Initial schema (<c>user_version</c> 1): the note envelope, the link graph, the
/// append-only event log, and the JSON Schema registry. FTS5 search is added by a later
/// migration so the ladder grows incrementally.
/// </summary>
public sealed class Migration0001Init : IMigration
{
    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public string Name => "0001_init";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
CREATE TABLE notes (
    id           TEXT PRIMARY KEY,
    domain       TEXT NOT NULL,
    type         TEXT NOT NULL,
    title        TEXT,
    body         TEXT,
    payload_json TEXT,
    tags_json    TEXT,
    dedup_key    TEXT,
    status       TEXT NOT NULL DEFAULT 'active',
    created_utc  TEXT NOT NULL,
    updated_utc  TEXT NOT NULL,
    source_agent TEXT,
    schema_ver   INTEGER NOT NULL DEFAULT 1,
    deleted      INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_notes_domain_type ON notes(domain, type, deleted);
CREATE UNIQUE INDEX ux_notes_dedup ON notes(domain, type, dedup_key) WHERE dedup_key IS NOT NULL;

CREATE TABLE note_links (
    from_id     TEXT NOT NULL,
    to_id       TEXT NOT NULL,
    rel         TEXT NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE INDEX ix_note_links_from ON note_links(from_id);
CREATE INDEX ix_note_links_to ON note_links(to_id);

CREATE TABLE note_events (
    event_id  TEXT PRIMARY KEY,
    note_id   TEXT NOT NULL,
    op        TEXT NOT NULL,
    actor     TEXT,
    ts        TEXT NOT NULL,
    diff_json TEXT
);

CREATE INDEX ix_note_events_note ON note_events(note_id, ts);

CREATE TABLE schemas (
    type        TEXT NOT NULL,
    version     INTEGER NOT NULL,
    json_schema TEXT NOT NULL,
    PRIMARY KEY (type, version)
);
";
}
