using System.Text;
using System.Text.Json.Nodes;
using MemoryMcp.Core.Notes;

namespace MemoryMcp.Core.Backlog;

/// <summary>
/// Renders backlog_item notes back into a flat markdown file (the human-readable view of the
/// structured truth — principle D11). Output is importer-compatible, so import/export round-trips.
/// </summary>
public static class BacklogExporter
{
    private static readonly (string Status, string Header, bool Detailed)[] Sections =
    {
        ("ready", "Ready", true),
        ("next", "Next", true),
        ("in_progress", "In progress", true),
        ("blocked", "Blocked", true),
        ("later", "Later", true),
        ("done", "Done", false),
    };

    /// <summary>Renders all backlog_item notes in the domain to markdown.</summary>
    /// <param name="repository">Source repository.</param>
    /// <param name="domain">Domain to export.</param>
    public static string Export(NotesRepository repository, string domain = "memory-mcp")
    {
        var grouped = new Dictionary<string, List<Note>>(StringComparer.Ordinal);
        foreach (var note in repository.List(domain, "backlog_item"))
        {
            var status = Field(note.PayloadJson, "status") ?? "later";
            if (!grouped.TryGetValue(status, out var list))
            {
                grouped[status] = list = new List<Note>();
            }

            list.Add(note);
        }

        var builder = new StringBuilder();
        builder.AppendLine("# Backlog (generated)");
        builder.AppendLine();

        foreach (var (status, header, detailed) in Sections)
        {
            if (!grouped.TryGetValue(status, out var group) || group.Count == 0)
            {
                continue;
            }

            builder.AppendLine($"## {header}");
            builder.AppendLine();

            foreach (var note in group.OrderBy(KeyOf, StringComparer.Ordinal))
            {
                var key = KeyOf(note);
                var title = string.IsNullOrWhiteSpace(note.Title) ? key : note.Title!;
                if (detailed)
                {
                    builder.AppendLine($"### {key}: {title}");
                    if (!string.IsNullOrWhiteSpace(note.Body))
                    {
                        builder.AppendLine(note.Body);
                    }

                    builder.AppendLine();
                }
                else
                {
                    builder.AppendLine($"- {key}: {title}");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string KeyOf(Note note) => note.DedupKey ?? Field(note.PayloadJson, "key") ?? note.Id;

    private static string? Field(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonNode.Parse(json)?[name]?.GetValue<string>();
    }
}
