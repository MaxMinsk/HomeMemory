using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Records which retrieval mapping each type's stored full-text lanes were computed from
/// (<c>user_version</c> 20, MEMP-262).
/// <para>Without it, "are these lanes still current?" is unanswerable, and the only honest response to an
/// edited mapping is to recompute the whole corpus every start. With it, a mapping change is detected per
/// type and only that type's notes are re-laned — the same shape the embedding index already uses to date its
/// passages.</para>
/// <para>Keyed by TYPE rather than by note: a mapping belongs to a type, so every note of that type becomes
/// stale together. Per-note tracking would store 1500 copies of the same answer.</para>
/// </summary>
public sealed class Migration0020LaneState : IMigration
{
    /// <inheritdoc />
    public int Version => 20;

    /// <inheritdoc />
    public string Name => "0020_lane_state";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
CREATE TABLE note_lane_state (
    type         TEXT PRIMARY KEY,
    mapping_hash TEXT NOT NULL,
    updated_utc  TEXT NOT NULL
);
";
        command.ExecuteNonQuery();
    }
}
