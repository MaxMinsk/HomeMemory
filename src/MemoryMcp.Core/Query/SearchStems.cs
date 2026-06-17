using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemoryMcp.Core.Query;

/// <summary>
/// Builds the stemmed-token text that feeds the FTS <c>stems</c> sidecar column (MEMP-024), and stems query
/// tokens the same way. Only NATURAL-LANGUAGE text is stemmed: title + body + tag values + payload string
/// <em>values</em>. Code blocks, inline code, URLs and paths are stripped first; only pure-letter tokens
/// (>= 2 letters, no digits/underscores/hyphens/dots) are stemmed — so note IDs, dedupKeys, JSON keys, tool/MCP
/// command names, file paths and versions are never stemmed (and they remain fully searchable via the raw FTS
/// columns, which this never touches).
/// </summary>
public static class SearchStems
{
    private const int MinStemLength = 2;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // Fenced code, inline code, then any whitespace-run containing '/', '\' or ':' (URLs, paths, key:value, times).
    private static readonly Regex FencedCode = new("```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex InlineCode = new("`[^`]*`", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex UrlOrPath = new(@"\S*[\\/:]\S*", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex TrimNonLetters = new(@"^\P{L}+|\P{L}+$", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The stemmed-token text to index for a note, or null when there is nothing natural-language to stem.</summary>
    /// <param name="title">Note title.</param>
    /// <param name="body">Note body.</param>
    /// <param name="tagsJson">Tags JSON (string values are used).</param>
    /// <param name="payloadJson">Payload JSON (string VALUES are used; keys are skipped).</param>
    public static string? For(string? title, string? body, string? tagsJson, string? payloadJson)
    {
        var sources = new List<string?> { title, body };
        sources.AddRange(JsonStringValues(tagsJson));
        sources.AddRange(JsonStringValues(payloadJson));
        // dedupKey is deliberately NOT a source (it is an identifier; it stays exact-searchable via raw FTS).

        var stems = new List<string>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (var word in StemmableWords(source))
            {
                stems.Add(SearchStemmer.Stem(word));
            }
        }

        return stems.Count == 0 ? null : string.Join(' ', stems);
    }

    /// <summary>Stems the stemmable subset of already-tokenized query terms (for the stems-column MATCH).</summary>
    /// <param name="tokens">Query tokens (e.g. from the snippet tokenizer).</param>
    public static IReadOnlyList<string> StemQueryTokens(IReadOnlyCollection<string> tokens)
    {
        var stems = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length >= MinStemLength && token.All(char.IsLetter))
            {
                stems.Add(SearchStemmer.Stem(token));
            }
        }

        return stems;
    }

    // Whitespace words from natural-language text (code/URLs/paths stripped), trimmed of edge punctuation, kept
    // only when the core is purely letters and >= MinStemLength (so IDs/identifiers/versions/paths are skipped).
    private static IEnumerable<string> StemmableWords(string text)
    {
        var cleaned = UrlOrPath.Replace(InlineCode.Replace(FencedCode.Replace(text, " "), " "), " ");
        foreach (var word in cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var core = TrimNonLetters.Replace(word, string.Empty);
            if (core.Length >= MinStemLength && IsAllLetters(core))
            {
                yield return core;
            }
        }
    }

    private static bool IsAllLetters(string word)
    {
        foreach (var ch in word)
        {
            if (!char.IsLetter(ch))
            {
                return false;
            }
        }

        return true;
    }

    // All string VALUES in a JSON document (object values recursively, array elements); keys are never yielded.
    private static IEnumerable<string> JsonStringValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var values = new List<string>();
            Collect(document.RootElement, values);
            return values;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void Collect(JsonElement element, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { } value)
                {
                    into.Add(value);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, into); // property.Name (the key) is intentionally skipped
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, into);
                }

                break;
            default:
                break;
        }
    }
}
