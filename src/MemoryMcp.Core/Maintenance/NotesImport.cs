using System.Text.Json.Nodes;
using MemoryMcp.Core.Notes;

namespace MemoryMcp.Core.Maintenance;

/// <summary>Result of an NDJSON import.</summary>
/// <param name="Total">Non-blank lines seen.</param>
/// <param name="Created">Notes that were (or would be) newly created.</param>
/// <param name="Updated">Notes that were (or would be) updated in place (by domain+type+dedupKey).</param>
/// <param name="Invalid">Lines that failed to parse, lacked domain/type, or failed payload validation.</param>
/// <param name="Applied">True when written; false for a dry run.</param>
public sealed record NotesImportReport(int Total, int Created, int Updated, int Invalid, bool Applied);

/// <summary>
/// Imports notes from NDJSON produced by <see cref="NotesExport"/> (MEMP-158). Idempotent: upserts by
/// (domain, type, dedupKey). A dry run validates + classifies each line (create vs update) without writing;
/// apply performs the upserts. Lines that don't parse, lack domain/type, or fail payload validation count as invalid.
/// </summary>
public static class NotesImport
{
    private enum Outcome
    {
        Invalid,
        Created,
        Updated,
    }

    /// <summary>Dry-runs (counts) or applies the import.</summary>
    /// <param name="notes">The note repository to upsert into.</param>
    /// <param name="ndjson">The NDJSON document (one note per line).</param>
    /// <param name="apply">When false, only validates/classifies; when true, writes.</param>
    public static NotesImportReport Run(NotesRepository notes, string ndjson, bool apply)
    {
        int total = 0, created = 0, updated = 0, invalid = 0;
        foreach (var raw in (ndjson ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            total++;
            switch (ImportLine(notes, line, apply))
            {
                case Outcome.Created: created++; break;
                case Outcome.Updated: updated++; break;
                default: invalid++; break;
            }
        }

        return new NotesImportReport(total, created, updated, invalid, apply);
    }

    private static Outcome ImportLine(NotesRepository notes, string line, bool apply)
    {
        JsonObject? row;
        try
        {
            row = JsonNode.Parse(line) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return Outcome.Invalid;
        }

        var domain = (string?)row?["domain"];
        var type = (string?)row?["type"];
        if (row is null || string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(type))
        {
            return Outcome.Invalid;
        }

        var payload = row["payload"] is { } p ? p.ToJsonString() : null;
        if (!notes.IsValidPayload(type, payload))
        {
            return Outcome.Invalid;
        }

        var dedupKey = (string?)row["dedupKey"];
        var exists = dedupKey is not null && notes.GetByDedupKey(domain, type, dedupKey) is not null;
        if (!apply)
        {
            return exists ? Outcome.Updated : Outcome.Created;
        }

        try
        {
            var result = notes.Upsert(domain, type, (string?)row["title"], (string?)row["body"], payload,
                row["tags"] is { } t ? t.ToJsonString() : null, dedupKey, (string?)row["sourceAgent"] ?? "import", (string?)row["project"]);
            return result.Created ? Outcome.Created : Outcome.Updated;
        }
        catch (NoteValidationException)
        {
            return Outcome.Invalid;
        }
    }
}
