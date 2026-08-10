using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// Unicode-aware case folding for SQL (MEMP-238). SQLite's built-in <c>LIKE</c>, <c>COLLATE NOCASE</c> and
/// <c>lower()</c> fold <b>ASCII A–Z only</b>, so every Cyrillic, Greek or accented-Latin comparison stayed
/// case-sensitive: the filter DSL's documented case-insensitive <c>contains</c> matched two Russian-titled notes
/// when the needle was typed in title case and none of them in lower case. These .NET-backed functions fold the whole
/// Unicode range and are registered on every connection by <see cref="SqliteConnectionFactory"/>:
/// <list type="bullet">
/// <item><description><c>mem_contains(haystack, needle)</c> — case-insensitive substring test (1/0).</description></item>
/// <item><description><c>mem_lower(value)</c> — full case fold, the replacement for <c>lower()</c> in comparisons.</description></item>
/// </list>
/// Neither can use an index, but neither could the <c>LIKE '%…%'</c> / <c>lower()</c> comparison it replaces,
/// so the change costs nothing on top of the scan that was already happening.
/// </summary>
public static class UnicodeSqlFunctions
{
    /// <summary>Registers the Unicode case-folding functions on an open connection.</summary>
    /// <param name="connection">An open connection; the functions live for its lifetime.</param>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Arguments are read as object, not string: a payload field may hold a number, and LIKE used to coerce
        // it to text. Text(...) keeps that coercion instead of throwing on a non-TEXT value.
        connection.CreateFunction<object?, object?, bool>(
            "mem_contains",
            (haystack, needle) => Contains(Text(haystack), Text(needle)),
            isDeterministic: true);

        connection.CreateFunction<object?, string?>(
            "mem_lower",
            value => Text(value)?.ToLowerInvariant(),
            isDeterministic: true);
    }

    /// <summary>Case-insensitive substring test; false when either side is SQL NULL, as <c>LIKE</c> was.</summary>
    /// <param name="haystack">The text searched.</param>
    /// <param name="needle">The substring looked for.</param>
    public static bool Contains(string? haystack, string? needle) =>
        haystack is not null && needle is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // SQLite hands us long/double/string/byte[]/null; render everything but NULL as invariant text.
    private static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
