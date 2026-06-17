namespace MemoryMcp.Core.Query;

/// <summary>
/// Compiles a user "sort" spec (e.g. <c>payload.spice_level desc</c>) into a safe SQL ORDER BY body
/// (MEMP-142). Only a whitelisted set of fields is sortable and the payload path is character-restricted,
/// so nothing user-supplied reaches SQL unescaped. NULLs always sort last. <c>json_extract</c> preserves
/// the JSON type, so numeric fields sort numerically and string fields lexicographically.
/// </summary>
public static class SortOrder
{
    /// <summary>Compiles the spec to an ORDER BY body (without the keywords), or null when no sort is given.
    /// Throws <see cref="FilterException"/> for an unsortable field.</summary>
    /// <param name="sort">e.g. <c>payload.spice_level desc</c>, <c>updated_utc asc</c>, <c>title</c>.</param>
    public static string? Compile(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return null;
        }

        var parts = sort.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var direction = parts.Length > 1 && parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        var expr = Expr(parts[0])
            ?? throw new FilterException(
                $"Cannot sort by '{parts[0]}'. Sortable fields: payload.<field>, title, updated_utc, created_utc (append asc/desc).");

        return $"({expr}) IS NULL, ({expr}) {direction}"; // NULLs last regardless of direction
    }

    private static string? Expr(string field) => field switch
    {
        "updated_utc" or "updatedUtc" => "n.updated_utc",
        "created_utc" or "createdUtc" => "n.created_utc",
        "title" => "n.title",
        _ => PayloadExpr(field),
    };

    private static string? PayloadExpr(string field)
    {
        const string prefix = "payload.";
        if (!field.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var path = field[prefix.Length..];
        if (path.Length == 0 || !path.All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
        {
            return null; // injection-safe: only [A-Za-z0-9_.] reaches the SQL json path
        }

        return $"json_extract(n.payload_json, '$.{path}')";
    }
}
