using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds full-text search (<c>user_version</c> 2): a contentless FTS5 index over note
/// title/body/tags, kept in sync with the <c>notes</c> table by triggers. Ranking uses
/// BM25. Row visibility (soft-deleted / archived) is enforced at query time via a join to
/// <c>notes</c>, not by removing rows from the index — this keeps the triggers trivial and
/// makes the query the single source of visibility truth.
/// </summary>
public sealed class Migration0002Fts : IMigration
{
    /// <inheritdoc />
    public int Version => 2;

    /// <inheritdoc />
    public string Name => "0002_fts";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, tags, content='');

CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, body, tags)
    VALUES (new.rowid, new.title, new.body, new.tags_json);
END;

CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json);
END;

CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, body, tags)
    VALUES ('delete', old.rowid, old.title, old.body, old.tags_json);
    INSERT INTO notes_fts(rowid, title, body, tags)
    VALUES (new.rowid, new.title, new.body, new.tags_json);
END;
";
}
