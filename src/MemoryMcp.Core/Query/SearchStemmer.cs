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

    // RU normalization (MEMP-192), all as \u escapes so the source stays ASCII:
    private const char Yo = '\u0451';   // RU letter yo
    private const char Ye = '\u0435';   // RU letter ye
    private const string FleetingSuffix = "\u0435\u0446";   // "ets" suffix kept by the nominative form
    private const string FleetingReplacement = "\u0446";    // "ts" — what oblique forms collapse to

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
            return CollapseFleetingVowel(_russian.Stem(lower.Replace(Yo, Ye))); // fold yo->ye, then collapse the fleeting vowel
        }

        _english ??= new EnglishStemmer();
        return _english.Stem(lower);
    }

    // Russian fleeting vowel: a nominative -ets that oblique forms drop to -ts (perec/perc, otec/otc, ogurec/ogurc).
    // Snowball leaves the nominative "ets" but strips oblique forms to "ts", so the dictionary form never matches its
    // own inflections. Collapsing the stem's trailing "ets" -> "ts" puts every form under one key. The -ets class is
    // reliably fleeting, so this is safe; non-fleeting stems (e.g. hleb, mesyac) do not end in "ets" and are untouched.
    private static string CollapseFleetingVowel(string stem) =>
        stem.Length > 2 && stem.EndsWith(FleetingSuffix, StringComparison.Ordinal)
            ? string.Concat(stem.AsSpan(0, stem.Length - FleetingSuffix.Length), FleetingReplacement)
            : stem;

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
