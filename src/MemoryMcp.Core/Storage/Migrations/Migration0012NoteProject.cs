using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds an optional <c>project</c> envelope column to notes (<c>user_version</c> 12): a sub-axis within a
/// domain (e.g. domain <c>development</c> with projects <c>memory-mcp</c>/<c>unity-solitaire</c>) so notes —
/// including memory_rule, whose payload can't carry it — are filterable/groupable by project. Backfilled from
/// <c>payload.project</c> where present. Project is organizational; scope stays at the domain level (MEMP-146).
/// </summary>
public sealed class Migration0012NoteProject : IMigration
{
    /// <inheritdoc />
    public int Version => 12;

    /// <inheritdoc />
    public string Name => "0012_note_project";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
ALTER TABLE notes ADD COLUMN project TEXT;
UPDATE notes SET project = lower(json_extract(payload_json, '$.project'))
    WHERE json_extract(payload_json, '$.project') IS NOT NULL;
CREATE INDEX ix_notes_domain_project_type ON notes(domain, project, type);";
        command.ExecuteNonQuery();
    }
}
