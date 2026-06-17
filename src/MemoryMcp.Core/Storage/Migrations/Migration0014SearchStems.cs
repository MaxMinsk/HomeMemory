using MemoryMcp.Core.Query;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds a stemmed-search sidecar (<c>user_version</c> 14, MEMP-024). A new <c>notes.search_stems</c> column holds
/// the Snowball-stemmed natural-language tokens of each note (computed in C#, since triggers can't), and the FTS
/// index gains a matching <c>stems</c> column. Raw columns (title/body/tags/dedup_key/payload) are UNCHANGED, so
/// exact/ID/code search is preserved; queries OR-in the stemmed column to also match word forms (ANRs/ANR,
/// Russian cases). Existing rows are backfilled, then the FTS is rebuilt to include the stems.
/// </summary>
public sealed class Migration0014SearchStems : IMigration
{
    /// <inheritdoc />
    public int Version => 14;

    /// <inheritdoc />
    public string Name => "0014_search_stems";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE notes ADD COLUMN search_stems TEXT;");

        // Backfill the column first (the old FTS triggers fire harmlessly: old==new for the indexed columns).
        Backfill(connection, transaction);

        // Rebuild FTS with the extra stems column + triggers that source it from new.search_stems, then reindex
        // from the now-populated columns (pure SQL, so no delete-on-empty corruption for the contentless index).
        Execute(connection, transaction, Rebuild);
    }

    private static void Backfill(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Id, string? Stems)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, title, body, tags_json, payload_json FROM notes;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var stems = SearchStems.For(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
                rows.Add((reader.GetString(0), stems));
            }
        }

        foreach (var (id, stems) in rows)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE notes SET search_stems = $s WHERE id = $id;";
            update.Parameters.AddWithValue("$s", (object?)stems ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private const string Rebuild = @"
DROP TRIGGER notes_ai;
DROP TRIGGER notes_ad;
DROP TRIGGER notes_au;
DROP TABLE notes_fts;

CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, tags, dedup_key, payload, stems, content='');

CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload, stems)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key, new.payload_json, new.search_stems);
END;

CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key, payload, stems)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key, old.payload_json, old.search_stems);
END;

CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key, payload, stems)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key, old.payload_json, old.search_stems);
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload, stems)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key, new.payload_json, new.search_stems);
END;

INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload, stems)
SELECT rowid, title, body, tags_json, dedup_key, payload_json, search_stems FROM notes;
";
}
