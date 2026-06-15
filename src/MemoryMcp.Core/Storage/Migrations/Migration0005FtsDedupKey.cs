using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Rebuilds the FTS index to also cover <c>dedup_key</c> (<c>user_version</c> 5), so notes are findable
/// by their stable key — e.g. searching "072" or "MEMP-072" matches a backlog item whose key is
/// <c>MEMP-072</c> (the key lives in the envelope/payload, which full-text search previously ignored).
/// </summary>
public sealed class Migration0005FtsDedupKey : IMigration
{
    /// <inheritdoc />
    public int Version => 5;

    /// <inheritdoc />
    public string Name => "0005_fts_dedup_key";

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

CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, tags, dedup_key, content='');

CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key);
END;

CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key);
END;

CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags, dedup_key)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json, old.dedup_key);
    INSERT INTO notes_fts(rowid, title, body, tags, dedup_key)
    VALUES (new.rowid, new.title, new.body, new.tags_json, new.dedup_key);
END;

INSERT INTO notes_fts(rowid, title, body, tags, dedup_key)
SELECT rowid, title, body, tags_json, dedup_key FROM notes;
";
}
