using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemoryMcp.Core.Notes;

namespace MemoryMcp.Core.Backlog;

/// <summary>
/// Parses a flat markdown backlog into <see cref="BacklogItem"/>s and upserts them as
/// <c>backlog_item</c> notes — the dogfood path that moves the backlog into memory.
/// </summary>
public static class BacklogImporter
{
    private static readonly Regex KeyRegex = new(@"[A-Z]+-\d{3,}", RegexOptions.Compiled);

    /// <summary>Parses markdown into backlog items (section heading drives the status).</summary>
    /// <param name="markdown">The backlog markdown text.</param>
    public static IReadOnlyList<BacklogItem> Parse(string markdown)
    {
        var items = new List<BacklogItem>();
        string? status = null;
        BacklogItem? current = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (current is not null)
            {
                items.Add(current with { Body = body.ToString().Trim() });
                current = null;
                body.Clear();
            }
        }

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                status = MapStatus(trimmed[3..].Trim());
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                var heading = trimmed[4..].Trim();
                var match = KeyRegex.Match(heading);
                if (status is not null && match.Success && heading.StartsWith(match.Value, StringComparison.Ordinal))
                {
                    current = new BacklogItem(match.Value, status, TitleAfterColon(heading), string.Empty);
                }

                continue;
            }

            if (current is not null)
            {
                body.AppendLine(line);
                continue;
            }

            if (status is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var content = trimmed[2..].Trim();
                var match = KeyRegex.Match(content);
                if (match.Success && content.StartsWith(match.Value, StringComparison.Ordinal))
                {
                    items.Add(new BacklogItem(match.Value, status, TitleAfterColon(content), string.Empty));
                }
            }
        }

        Flush();
        return items;
    }

    /// <summary>Parses and upserts the backlog into memory. Idempotent (upsert by key).</summary>
    /// <param name="repository">Target repository.</param>
    /// <param name="markdown">Backlog markdown text.</param>
    /// <param name="domain">Domain to import into.</param>
    /// <param name="sourceAgent">Provenance.</param>
    /// <returns>The number of items imported.</returns>
    public static int Import(NotesRepository repository, string markdown,
        string domain = "memory-mcp", string sourceAgent = "backlog-importer")
    {
        var items = Parse(markdown);
        foreach (var item in items)
        {
            var payload = JsonSerializer.Serialize(new { key = item.Key, status = item.Status });
            repository.Upsert(domain, "backlog_item", item.Title,
                string.IsNullOrWhiteSpace(item.Body) ? null : item.Body, payload, null, item.Key, sourceAgent);
        }

        return items.Count;
    }

    private static string TitleAfterColon(string text)
    {
        var colon = text.IndexOf(':');
        return colon >= 0 ? text[(colon + 1)..].Trim() : text;
    }

    private static string? MapStatus(string header) => header.ToLowerInvariant() switch
    {
        "done" => "done",
        "ready" => "ready",
        "next" => "next",
        "later" => "later",
        "in progress" or "in_progress" => "in_progress",
        "blocked" => "blocked",
        _ => null,
    };
}
