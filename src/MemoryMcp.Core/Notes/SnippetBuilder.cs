using System.Text.RegularExpressions;

namespace MemoryMcp.Core.Notes;

/// <summary>Pure helpers for full-text query tokenization and snippet rendering
/// (contentless FTS5 can't produce snippets, so we derive them from stored text).</summary>
internal static class SnippetBuilder
{
    /// <summary>Splits raw user text into word tokens (used to build a safe FTS5 phrase query and to locate snippets).</summary>
    public static List<string> Tokenize(string raw) =>
        Regex.Matches(raw, @"\w+").Select(match => match.Value).ToList();

    /// <summary>Builds a short excerpt around the first matching token (bracketed), or a head excerpt if none match.</summary>
    public static string? Build(string? title, string? body, IReadOnlyList<string> tokens)
    {
        var text = !string.IsNullOrWhiteSpace(body) ? body : title;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const int window = 120;
        foreach (var token in tokens)
        {
            var index = text!.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = Math.Max(0, index - 40);
            var length = Math.Min(text.Length - start, window);
            var fragment = text.Substring(start, length);

            var relative = fragment.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (relative >= 0)
            {
                fragment = fragment[..relative] + "[" + fragment.Substring(relative, token.Length) + "]" +
                           fragment[(relative + token.Length)..];
            }

            var prefix = start > 0 ? "…" : string.Empty;
            var suffix = start + length < text.Length ? "…" : string.Empty;
            return prefix + fragment + suffix;
        }

        return text!.Length <= window ? text : text[..window] + "…";
    }
}
