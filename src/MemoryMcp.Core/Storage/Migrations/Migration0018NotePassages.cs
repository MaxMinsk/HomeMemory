using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds the <c>note_passages</c> table (<c>user_version</c> 18): the opt-in embedding index (MEMP-196).
/// <para>A note stores SEVERAL rows, not one. Measured on the golden set, a single mean-pooled vector per note
/// is worse than indexing the title alone — the note's topic drifts toward a generic centroid and a short query
/// stops landing near it. Passages, scored by the best one, fixed the tail (a one-word-titled note went from
/// rank 143 to 12) and cut mean rank from 23.0 to 17.2.</para>
/// <para>Each row records what produced it — model, mapping hash, source paths and a content hash — so a hit
/// can name the field it came from, a model or mapping change invalidates exactly the affected rows instead of
/// forcing a full rebuild, and two different vector spaces can never be compared by accident.</para>
/// </summary>
public sealed class Migration0018NotePassages : IMigration
{
    /// <inheritdoc />
    public int Version => 18;

    /// <inheritdoc />
    public string Name => "0018_note_passages";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
CREATE TABLE note_passages (
    note_id       TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    passage_name  TEXT NOT NULL,
    passage_ord   INTEGER NOT NULL,
    model_id      TEXT NOT NULL,
    dimensions    INTEGER NOT NULL,
    mapping_hash  TEXT NOT NULL,
    content_hash  TEXT NOT NULL,
    source_paths  TEXT NOT NULL,
    vector        BLOB NOT NULL,
    updated_utc   TEXT NOT NULL,
    PRIMARY KEY (note_id, passage_name, passage_ord)
);

-- Rebuild-by-note (a write replaces all of a note's passages) and staleness sweeps by model/mapping.
CREATE INDEX ix_note_passages_model ON note_passages(model_id, mapping_hash);";
        command.ExecuteNonQuery();
    }
}
