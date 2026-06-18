using System.Text.Json.Nodes;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Maintenance;

/// <summary>
/// Exports notes as NDJSON for backup/portability (MEMP-157): one JSON note per line (envelope + parsed payload +
/// tags), non-deleted rows, optionally scoped to a domain, in a deterministic order. Round-trips with
/// <see cref="NotesImport"/>. Bodies are Markdown already; no extra secrets beyond note content.
/// </summary>
public static class NotesExport
{
    /// <summary>Yields one NDJSON line per note (streamable — the DB cursor stays open during enumeration).</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="domain">Optional domain scope (null = all domains).</param>
    public static IEnumerable<string> Lines(ISqliteConnectionFactory connectionFactory, string? domain)
    {
        var scoped = string.IsNullOrWhiteSpace(domain) ? null : Identifiers.Normalize(domain);
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {NoteRowMapper.Columns} FROM notes WHERE deleted = 0 " +
            (scoped is null ? string.Empty : "AND domain = $d ") +
            "ORDER BY domain, created_utc, id;";
        if (scoped is not null)
        {
            command.Parameters.AddWithValue("$d", scoped);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return ToLine(NoteRowMapper.Map(reader));
        }
    }

    private static string ToLine(Note note)
    {
        var line = new JsonObject
        {
            ["id"] = note.Id,
            ["domain"] = note.Domain,
            ["type"] = note.Type,
            ["title"] = note.Title,
            ["body"] = note.Body,
            ["payload"] = TryParse(note.PayloadJson),
            ["tags"] = TryParse(note.TagsJson),
            ["dedupKey"] = note.DedupKey,
            ["sourceAgent"] = note.SourceAgent,
            ["project"] = note.Project,
            ["status"] = note.Status,
            ["schemaVer"] = note.SchemaVer,
            ["createdUtc"] = note.CreatedUtc,
            ["updatedUtc"] = note.UpdatedUtc,
        };
        return line.ToJsonString();
    }

    private static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json; // keep malformed payload as a string so export never fails
        }
    }
}
