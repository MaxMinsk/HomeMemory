using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds the <c>agent_reads</c> table (<c>user_version</c> 17): a per-agent running count of recall/search
/// reads (only for callers that identify themselves with a sourceAgent). Writes are already attributable via
/// <c>note_events.actor</c>; this fills in the read side so the adoption report can show "who writes without
/// reading" (MEMP-207). Volatile-ish by design — only identified reads are counted, so volume stays low.
/// </summary>
public sealed class Migration0017AgentReads : IMigration
{
    /// <inheritdoc />
    public int Version => 17;

    /// <inheritdoc />
    public string Name => "0017_agent_reads";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
CREATE TABLE agent_reads (
    agent         TEXT PRIMARY KEY,
    read_count    INTEGER NOT NULL DEFAULT 0,
    last_read_utc TEXT NOT NULL
);";
        command.ExecuteNonQuery();
    }
}
