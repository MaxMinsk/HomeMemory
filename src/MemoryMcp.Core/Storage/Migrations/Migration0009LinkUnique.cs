using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// De-duplicates <c>note_links</c> and adds a UNIQUE index on <c>(from_id, to_id, rel)</c>
/// (<c>user_version</c> 9), so a relation between two notes exists at most once and linking becomes
/// idempotent (INSERT OR IGNORE). Removes any pre-existing duplicate edges first (MEMP-101).
/// </summary>
public sealed class Migration0009LinkUnique : IMigration
{
    /// <inheritdoc />
    public int Version => 9;

    /// <inheritdoc />
    public string Name => "0009_link_unique";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
DELETE FROM note_links
WHERE rowid NOT IN (SELECT min(rowid) FROM note_links GROUP BY from_id, to_id, rel);

CREATE UNIQUE INDEX ux_note_links ON note_links (from_id, to_id, rel);
";
}
