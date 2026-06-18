using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Indexes payload VALUES, not keys, in the raw FTS (<c>user_version</c> 15, MEMP-152). The <c>payload</c> FTS
/// column was fed the whole <c>payload_json</c> (keys + values + IDs), so searching a field NAME like <c>status</c>
/// matched every note that merely has that field (noise). Now it is fed only the payload's string VALUES —
/// <c>json_tree(payload_json)</c> rows of type <c>text</c> (object keys live in json_tree's <c>key</c> column and
/// are never yielded as values) — so values stay searchable while key names drop out. Triggers are recreated and
/// the contentless index rebuilt. COALESCE guards a NULL payload (e.g. journal notes) from json_tree.
/// </summary>
public sealed class Migration0015PayloadValuesFts : IMigration
{
    /// <inheritdoc />
    public int Version => 15;

    /// <inheritdoc />
    public string Name => "0015_payload_values_fts";

    // Space-joined payload string VALUES (not keys); NULL/empty payload yields no text rows -> NULL column.
    private static string PayloadValues(string column) =>
        $"(SELECT group_concat(value, ' ') FROM json_tree(COALESCE({column}, '{{}}')) WHERE type = 'text')";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        var fromNew = PayloadValues("new.payload_json");
        var fromOld = PayloadValues("old.payload_json");
        var fromRow = PayloadValues("payload_json");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $@"
DROP TRIGGER notes_ai;
DROP TRIGGER notes_ad;
DROP TRIGGER notes_au;
DROP TABLE notes_fts;

CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, tags, dedup_key, payload, stems, content='');

CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload, stems)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key, {fromNew}, new.search_stems);
END;

CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key, payload, stems)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key, {fromOld}, old.search_stems);
END;

CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key, payload, stems)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key, {fromOld}, old.search_stems);
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload, stems)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key, {fromNew}, new.search_stems);
END;

INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload, stems)
SELECT rowid, title, body, tags_json, dedup_key, {fromRow}, search_stems FROM notes;
";
        command.ExecuteNonQuery();
    }
}
