using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds the <c>note_usage</c> table (<c>user_version</c> 10): lightweight access signals (last accessed,
/// retrieval count) recorded on explicit reads, kept separate from the note content/audit so reads don't
/// mutate notes. Powers recency/most-used discovery (MEMP-116).
/// </summary>
public sealed class Migration0010NoteUsage : IMigration
{
    /// <inheritdoc />
    public int Version => 10;

    /// <inheritdoc />
    public string Name => "0010_note_usage";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
CREATE TABLE note_usage (
    note_id           TEXT PRIMARY KEY,
    last_accessed_utc TEXT NOT NULL,
    retrieval_count   INTEGER NOT NULL DEFAULT 0
);";
        command.ExecuteNonQuery();
    }
}
