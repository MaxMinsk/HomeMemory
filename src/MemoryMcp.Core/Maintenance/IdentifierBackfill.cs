using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Maintenance;

/// <summary>The result of an identifier backfill pass.</summary>
/// <param name="NotesUpdated">Notes whose domain/type/tags were normalized (or would be, in a dry run).</param>
/// <param name="AttachmentsUpdated">Attachment rows whose domain was lowercased.</param>
/// <param name="Collisions">Note ids skipped because normalizing collided with an existing (domain,type,dedupKey).</param>
/// <param name="Applied">Whether changes were written (false = dry run).</param>
public sealed record BackfillReport(int NotesUpdated, int AttachmentsUpdated, IReadOnlyList<string> Collisions, bool Applied);

/// <summary>
/// One-off backfill that brings EXISTING rows in line with the write-time normalization (MEMP-064):
/// lowercases note domain/type, canonicalizes tags, and lowercases attachment domains. Collision-aware —
/// a note whose normalized (domain,type,dedupKey) already exists is left untouched and reported. Admin/ops only.
/// </summary>
public static class IdentifierBackfill
{
    /// <summary>Runs the backfill. When <paramref name="apply"/> is false, reports counts without writing.</summary>
    public static BackfillReport Run(ISqliteConnectionFactory connectionFactory, bool apply)
    {
        using var connection = connectionFactory.Create();
        var (notesUpdated, collisions) = BackfillNotes(connection, apply);
        var attachmentsUpdated = BackfillAttachments(connection, apply);
        return new BackfillReport(notesUpdated, attachmentsUpdated, collisions, apply);
    }

    private static (int Updated, List<string> Collisions) BackfillNotes(SqliteConnection connection, bool apply)
    {
        var rows = new List<(string Id, string Domain, string Type, string? Tags)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, domain, type, tags_json FROM notes;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var updated = 0;
        var collisions = new List<string>();
        foreach (var row in rows)
        {
            var domain = Identifiers.Normalize(row.Domain);
            var type = Identifiers.Normalize(row.Type);
            var tags = Identifiers.NormalizeTags(row.Tags);
            if (domain == row.Domain && type == row.Type && string.Equals(tags, row.Tags, StringComparison.Ordinal))
            {
                continue;
            }

            if (!apply)
            {
                updated++;
                continue;
            }

            try
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE notes SET domain = $d, type = $t, tags_json = $g WHERE id = $id;";
                update.Parameters.AddWithValue("$d", domain);
                update.Parameters.AddWithValue("$t", type);
                update.Parameters.AddWithValue("$g", (object?)tags ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
                updated++;
            }
            catch (SqliteException)
            {
                collisions.Add(row.Id); // normalized key already exists — needs a manual merge
            }
        }

        return (updated, collisions);
    }

    private static int BackfillAttachments(SqliteConnection connection, bool apply)
    {
        using var command = connection.CreateCommand();
        command.CommandText = apply
            ? "UPDATE attachments SET domain = lower(domain) WHERE domain <> lower(domain);"
            : "SELECT count(*) FROM attachments WHERE domain <> lower(domain);";
        return apply ? command.ExecuteNonQuery() : Convert.ToInt32(command.ExecuteScalar());
    }
}
