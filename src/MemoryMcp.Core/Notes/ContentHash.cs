using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Deterministic content hash of a note's semantic content (MEMP-035): type + title + body + payload + tags,
/// with the JSON canonicalized (object keys sorted, whitespace dropped) so payloads that are equal but written
/// differently hash the same. Stored per note so exact-content duplicates are detectable (the
/// <c>duplicate_content</c> lint and capture-help compare hashes). Stable across processes — plain SHA-256 hex.
/// </summary>
public static class ContentHash
{
    /// <summary>Computes the lowercase hex SHA-256 of the note's canonical content.</summary>
    /// <param name="type">The note type.</param>
    /// <param name="title">The note title (trimmed; null treated as empty).</param>
    /// <param name="body">The note body (null treated as empty).</param>
    /// <param name="payloadJson">The typed payload JSON (canonicalized; raw if unparseable).</param>
    /// <param name="tagsJson">The tags JSON (canonicalized; raw if unparseable).</param>
    public static string Compute(string type, string? title, string? body, string? payloadJson, string? tagsJson)
    {
        var canonical = string.Join('\n',
            type ?? string.Empty,
            (title ?? string.Empty).Trim(),
            body ?? string.Empty,
            Canonical(payloadJson),
            Canonical(tagsJson));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // A stable string form of a JSON value: object keys sorted, no insignificant whitespace. Falls back to the
    // trimmed raw text when the input is not valid JSON, so a non-JSON payload still hashes deterministically.
    private static string Canonical(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            Write(document.RootElement, builder);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }

                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Write(property.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(item, builder);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
