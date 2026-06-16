using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Rebuilds the FTS index to also cover <c>payload_json</c> (<c>user_version</c> 8), so values stored in a
/// note's typed payload become full-text searchable — e.g. a <c>russian_name</c>/<c>english_name</c> field
/// is found by <c>notes_search</c>, not only by the exact filter DSL. Title/body/tags/dedup_key stay indexed.
/// </summary>
public sealed class Migration0008FtsPayload : IMigration
{
    /// <inheritdoc />
    public int Version => 8;

    /// <inheritdoc />
    public string Name => "0008_fts_payload";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
DROP TRIGGER notes_ai;
DROP TRIGGER notes_ad;
DROP TRIGGER notes_au;
DROP TABLE notes_fts;

CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, tags, dedup_key, payload, content='');

CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key, new.payload_json);
END;

CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key, payload)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key, old.payload_json);
END;

CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key, payload)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key, old.payload_json);
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key, new.payload_json);
END;

INSERT INTO notes_fts(rowid, title, body, tags, dedup_key, payload)
SELECT rowid, title, body, tags_json, dedup_key, payload_json FROM notes;
";
}
