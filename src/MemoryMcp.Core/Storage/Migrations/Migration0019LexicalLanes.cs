using MemoryMcp.Core.Retrieval;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Replaces the fixed full-text columns with LANES a type's schema assigns text to (<c>user_version</c> 19,
/// MEMP-262).
/// <para>Before this, the index had a column per SOURCE — body here, payload there — so every type's payload
/// was worth the same whatever it contained, and the only way to change that was to edit C#. The lanes are
/// named for what text DOES (<c>primary_text</c> says what a note is about, <c>secondary_text</c> makes it
/// findable) and a type's <c>x-retrieval</c> annotations decide which of its fields land where.</para>
/// <para><b>This migration is deliberately behaviour-neutral.</b> The new lanes ship with exactly the weights
/// the old columns had, so ranking cannot move on the day the table changes. Re-weighting is a query-time
/// change needing no reindex, so it can be made afterwards, measured against the golden set, and reverted
/// without touching the database — which is the whole reason the two were separated.</para>
/// <para><b>The backfill uses the legacy split on purpose.</b> Computing the schema-aware split here would
/// need the agent-authored schemas, which live in a table this migration is in the middle of rebuilding
/// around. It is also pointless while both lanes weigh the same: the split only becomes observable when the
/// weights diverge, and the one-off rebuild that precedes that is where precision is needed. Notes written
/// after this migration get the precise split immediately.</para>
/// </summary>
public sealed class Migration0019LexicalLanes : IMigration
{
    /// <inheritdoc />
    public int Version => 19;

    /// <inheritdoc />
    public string Name => "0019_lexical_lanes";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumns(connection, transaction);
        Backfill(connection, transaction);
        Rebuild(connection, transaction);
    }

    private static void AddColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
ALTER TABLE notes ADD COLUMN lane_primary TEXT;
ALTER TABLE notes ADD COLUMN lane_secondary TEXT;
";
        command.ExecuteNonQuery();
    }

    // Read-then-write, matching migration 0016: the split is C# logic, so it cannot be expressed as SQL.
    private static void Backfill(SqliteConnection connection, SqliteTransaction transaction)
    {
        var projector = new LegacyRetrievalProjector();
        var rows = new List<(string Id, string? Primary, string? Secondary)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, type, title, body, tags_json, payload_json FROM notes;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var lanes = projector.Lanes(new NoteContent(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
                rows.Add((reader.GetString(0), lanes.Primary, lanes.Secondary));
            }
        }

        foreach (var (id, primary, secondary) in rows)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE notes SET lane_primary = $p, lane_secondary = $s WHERE id = $id;";
            update.Parameters.AddWithValue("$p", (object?)primary ?? DBNull.Value);
            update.Parameters.AddWithValue("$s", (object?)secondary ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }

    // The SQL fallback the triggers keep. A write that goes around NotesWriter — a migration, an import, an
    // admin tool, a test — cannot compute the schema-aware split, and without this its body and payload would
    // simply stop being searchable, silently. So the lanes OVERRIDE the old extraction rather than replacing
    // it: a precise split when one was computed, the previous coarse one otherwise. Strictly additive.
    private static string PayloadValues(string column) =>
        $"(SELECT group_concat(value, ' ') FROM json_tree(COALESCE({column}, '{{}}')) WHERE type = 'text')";

    // The FTS table is entirely derived, so it is dropped and rebuilt rather than migrated.
    private static void Rebuild(SqliteConnection connection, SqliteTransaction transaction)
    {
        var newPrimary = "COALESCE(new.lane_primary, new.body)";
        var oldPrimary = "COALESCE(old.lane_primary, old.body)";
        var newSecondary = $"COALESCE(new.lane_secondary, {PayloadValues("new.payload_json")})";
        var oldSecondary = $"COALESCE(old.lane_secondary, {PayloadValues("old.payload_json")})";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $@"
DROP TRIGGER notes_ai;
DROP TRIGGER notes_ad;
DROP TRIGGER notes_au;
DROP TABLE notes_fts;

CREATE VIRTUAL TABLE notes_fts USING fts5(identity, title, primary_text, secondary_text, tags, stems, content='');

CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, identity, title, primary_text, secondary_text, tags, stems)
    VALUES (new.rowid, new.dedup_key, new.title, {newPrimary}, {newSecondary}, new.tags_json, new.search_stems);
END;

CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, identity, title, primary_text, secondary_text, tags, stems)
    VALUES ('delete', old.rowid, old.dedup_key, old.title, {oldPrimary}, {oldSecondary}, old.tags_json, old.search_stems);
END;

CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, identity, title, primary_text, secondary_text, tags, stems)
    VALUES ('delete', old.rowid, old.dedup_key, old.title, {oldPrimary}, {oldSecondary}, old.tags_json, old.search_stems);
    INSERT INTO notes_fts(rowid, identity, title, primary_text, secondary_text, tags, stems)
    VALUES (new.rowid, new.dedup_key, new.title, {newPrimary}, {newSecondary}, new.tags_json, new.search_stems);
END;

INSERT INTO notes_fts(rowid, identity, title, primary_text, secondary_text, tags, stems)
SELECT rowid, dedup_key, title, COALESCE(lane_primary, body), COALESCE(lane_secondary, {PayloadValues("payload_json")}), tags_json, search_stems FROM notes;
";
        command.ExecuteNonQuery();
    }
}
