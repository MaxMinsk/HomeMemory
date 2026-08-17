using System.Text.RegularExpressions;
using MemoryMcp.Core.Retrieval;

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
    private static readonly LegacyRetrievalProjector LegacyProjector = new();

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
    public static string? For(string? title, string? body, string? tagsJson, string? payloadJson) =>
        From(LegacyProjector.Lexical(new NoteContent(string.Empty, title, body, tagsJson, payloadJson)));

    /// <summary>
    /// The stemmed-token text for sources already projected by an <see cref="IRetrievalProjector"/> (MEMP-251).
    /// This is the real implementation; <see cref="For"/> is the shorthand that projects with the legacy
    /// mapping. Which text a note contributes is the projector's decision, not this method's — stemming only
    /// decides which WORDS within that text are natural language.
    /// </summary>
    /// <param name="sources">The note's projected text, in index order.</param>
    public static string? From(IReadOnlyList<RetrievalText> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        // dedupKey is deliberately NOT a source (it is an identifier; it stays exact-searchable via raw FTS).
        var stems = new List<string>();
        foreach (var source in sources)
        {
            foreach (var word in StemmableWords(source.Text))
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
}
