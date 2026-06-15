using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemoryMcp.Core.Naming;

/// <summary>
/// Normalizes case-insensitive identifiers (domain, type, tags) so <c>Home</c> and <c>home</c> are the
/// same domain. Applied at the write boundary and on read filters, so callers needn't worry about casing
/// and the store never accumulates case-variant namespaces.
/// </summary>
public static class Identifiers
{
    /// <summary>Trims and lowercases a required identifier (domain/type).</summary>
    public static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Trims and lowercases an optional identifier/filter; null or blank passes through.</summary>
    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Lowercases/trims each tag and drops blanks and duplicates. Returns the input unchanged if it is
    /// not a JSON array (so malformed input fails later, where it is reported, rather than silently here).
    /// </summary>
    public static string? NormalizeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return tagsJson;
        }

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(tagsJson) as JsonArray;
        }
        catch (JsonException)
        {
            return tagsJson;
        }

        if (array is null)
        {
            return tagsJson;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new JsonArray();
        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var raw))
            {
                var tag = raw.Trim().ToLowerInvariant();
                if (tag.Length > 0 && seen.Add(tag))
                {
                    normalized.Add(tag);
                }
            }
        }

        return normalized.ToJsonString();
    }
}
