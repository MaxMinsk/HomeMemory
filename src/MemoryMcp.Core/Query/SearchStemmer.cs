using Snowball;

namespace MemoryMcp.Core.Query;

/// <summary>
/// Per-token Snowball stemmer routed by alphabet (MEMP-024): Cyrillic tokens use the Russian stemmer, everything
/// else the English one. Stems are a lexical-normalization SIDECAR — raw tokens are still indexed and matched, so
/// this never changes exact/ID/code search; it only adds "word-form" recall (e.g. ANRs vs ANR, or Russian cases).
/// Snowball stemmers are stateful, so instances are thread-local.
/// </summary>
public static class SearchStemmer
{
    // Unicode Cyrillic block U+0400..U+04FF (numeric code points keep this file ASCII for the English gate).
    private const int CyrillicFirst = 0x0400;
    private const int CyrillicLast = 0x04FF;

    [ThreadStatic] private static EnglishStemmer? _english;
    [ThreadStatic] private static RussianStemmer? _russian;

    /// <summary>Stems one lowercased word; routes by script. Callers pass only pure-letter tokens.</summary>
    /// <param name="token">A word token (already trimmed of surrounding punctuation).</param>
    public static string Stem(string token)
    {
        var lower = token.ToLowerInvariant();
        if (IsCyrillic(lower))
        {
            _russian ??= new RussianStemmer();
            return _russian.Stem(lower);
        }

        _english ??= new EnglishStemmer();
        return _english.Stem(lower);
    }

    private static bool IsCyrillic(string token)
    {
        foreach (var ch in token)
        {
            if (ch >= CyrillicFirst && ch <= CyrillicLast)
            {
                return true;
            }
        }

        return false;
    }
}
