using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds provenance columns to the <c>schemas</c> table (<c>user_version</c> 11): <c>author</c> (who
/// registered the schema — <c>system</c> for built-ins, the <c>sourceAgent</c> for agent-authored ones)
/// and <c>updated_utc</c>. Audit only — schema authoring stays open and unrestricted (MEMP-122).
/// </summary>
public sealed class Migration0011SchemaProvenance : IMigration
{
    /// <inheritdoc />
    public int Version => 11;

    /// <inheritdoc />
    public string Name => "0011_schema_provenance";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
ALTER TABLE schemas ADD COLUMN author TEXT;
ALTER TABLE schemas ADD COLUMN updated_utc TEXT;";
        command.ExecuteNonQuery();
    }
}
