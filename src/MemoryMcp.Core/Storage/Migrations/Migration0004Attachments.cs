using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds the <c>attachments</c> table (<c>user_version</c> 4): metadata rows that reference
/// content-addressed blobs (by sha256) stored on disk, optionally linked to a note. Bytes live in
/// the blob store, never in SQLite; this table only records identity, size and provenance so agents
/// can list/inspect attachments without the bytes ever passing through the model context.
/// </summary>
public sealed class Migration0004Attachments : IMigration
{
    /// <inheritdoc />
    public int Version => 4;

    /// <inheritdoc />
    public string Name => "0004_attachments";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
CREATE TABLE attachments (
    id           TEXT PRIMARY KEY,
    domain       TEXT NOT NULL,
    sha256       TEXT NOT NULL,
    note_id      TEXT,
    filename     TEXT,
    content_type TEXT,
    size_bytes   INTEGER NOT NULL,
    source_agent TEXT,
    created_utc  TEXT NOT NULL
);

CREATE INDEX ix_attachments_domain ON attachments (domain, created_utc);
CREATE INDEX ix_attachments_note ON attachments (note_id);
CREATE INDEX ix_attachments_sha ON attachments (sha256);
";
}
