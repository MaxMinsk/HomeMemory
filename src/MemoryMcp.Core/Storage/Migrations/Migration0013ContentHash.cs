using MemoryMcp.Core.Notes;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds a deterministic <c>content_hash</c> column to notes (<c>user_version</c> 13, MEMP-035): the SHA-256 of a
/// note's canonical content (type+title+body+payload+tags). Indexed by (domain, content_hash) so exact-content
/// duplicates within a domain are cheap to find (the <c>duplicate_content</c> lint, capture-help). Existing rows
/// are backfilled here so the hash is complete from day one; every later write keeps it current.
/// </summary>
public sealed class Migration0013ContentHash : IMigration
{
    /// <inheritdoc />
    public int Version => 13;

    /// <inheritdoc />
    public string Name => "0013_content_hash";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText =
                "ALTER TABLE notes ADD COLUMN content_hash TEXT;" +
                "CREATE INDEX ix_notes_content_hash ON notes(domain, content_hash);";
            schema.ExecuteNonQuery();
        }

        Backfill(connection, transaction);
    }

    // Computes content_hash for every existing row (one-time). Read the rows first, then update — SQLite forbids
    // writing a table while a read cursor over it is open.
    private static void Backfill(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Id, string Hash)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, type, title, body, payload_json, tags_json FROM notes;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var hash = ContentHash.Compute(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
                rows.Add((reader.GetString(0), hash));
            }
        }

        foreach (var (id, hash) in rows)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE notes SET content_hash = $h WHERE id = $id;";
            update.Parameters.AddWithValue("$h", hash);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }
}
