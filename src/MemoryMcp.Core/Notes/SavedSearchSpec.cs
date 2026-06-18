using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// The parsed parameters of a <c>saved_search</c> note (MEMP-185): a named, reusable query. Parsing is lenient —
/// missing or malformed fields simply stay null so a partial saved search still runs.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Query">Full-text query.</param>
/// <param name="Domain">Target domain (null = run against the saved_search note's own domain).</param>
/// <param name="Type">Type filter.</param>
/// <param name="Tags">Tags filter (all must be present).</param>
/// <param name="Filter">Filter DSL expression.</param>
/// <param name="Sort">Sort spec.</param>
/// <param name="Rank">Relevance mode (lexical/hybrid).</param>
/// <param name="Limit">Page size.</param>
public sealed record SavedSearchSpec(
    string? Name, string? Query, string? Domain, string? Type, IReadOnlyList<string>? Tags,
    string? Filter, string? Sort, string? Rank, int? Limit)
{
    /// <summary>Parses a saved_search payload into a spec; returns an all-null spec when the payload is missing/invalid.</summary>
    /// <param name="payloadJson">The note's payload JSON.</param>
    public static SavedSearchSpec Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new SavedSearchSpec(null, null, null, null, null, null, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SavedSearchSpec(null, null, null, null, null, null, null, null, null);
            }

            return new SavedSearchSpec(
                Str(root, "name"), Str(root, "query"), Str(root, "domain"), Str(root, "type"),
                ParseTags(root), Str(root, "filter"), Str(root, "sort"), Str(root, "rank"), Int(root, "limit"));
        }
        catch (JsonException)
        {
            return new SavedSearchSpec(null, null, null, null, null, null, null, null, null);
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    private static IReadOnlyList<string>? ParseTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tags = value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToList();
        return tags.Count == 0 ? null : tags;
    }
}
