using MemoryMcp.Core.Query;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// The title-match ranking signal (MEMP-237). Before this, the only title-aware boost was an exact whole-title
/// match (MEMP-160): a note whose title <em>is</em> the query floated to the top, and a note whose title merely
/// <em>contains</em> the query got nothing — so a recipe whose title OPENS with the searched word ranked below
/// notes that name it once in their body. This scores a partial match as a graded signal: what share of the query's words the
/// title covers, plus a bonus when the title <em>opens</em> with one of them (a title that leads with the term
/// is more about it than one that mentions it at the end).
/// <para>Matching mirrors the FTS query so the signal agrees with what was searched: a title word matches by
/// prefix (as FTS5 <c>"token"*</c> does) or by sharing a stem (as the <c>stems</c> sidecar does). Comparison is
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, which — unlike SQL's <c>lower()</c> — folds Cyrillic too.</para>
/// </summary>
internal static class TitleRelevance
{
    /// <summary>Extra goodness for a title that starts with a query word, on top of the 0..1 coverage.</summary>
    private const double LeadBonus = 0.25;

    private const int MinStemLength = 2;

    /// <summary>One query word paired with its stem (null when the word is not natural language).</summary>
    /// <param name="Token">The raw query token.</param>
    /// <param name="Stem">Its stem, or null for identifiers/numbers that are never stemmed.</param>
    internal readonly record struct QueryTerm(string Token, string? Stem);

    /// <summary>
    /// Prepares the query side once per search. Stemming is per-token work and the candidate pool is up to
    /// <see cref="RankingWeights.PoolSize"/> rows, so the query terms are stemmed once, not once per row.
    /// </summary>
    /// <param name="tokens">The search's query tokens.</param>
    public static IReadOnlyList<QueryTerm> Terms(IReadOnlyList<string> tokens)
    {
        var terms = new List<QueryTerm>(tokens.Count);
        foreach (var token in tokens)
        {
            terms.Add(new QueryTerm(token, Stemmable(token) ? SearchStemmer.Stem(token) : null));
        }

        return terms;
    }

    /// <summary>
    /// Title goodness in [0, 1.25]: the share of query terms the title covers, plus <see cref="LeadBonus"/> when
    /// the title's first word is one of them. 0 when there is no title, no query, or no overlap.
    /// </summary>
    /// <param name="title">The note's title.</param>
    /// <param name="terms">The prepared query terms (see <see cref="Terms"/>).</param>
    public static double Goodness(string? title, IReadOnlyList<QueryTerm> terms)
    {
        if (string.IsNullOrWhiteSpace(title) || terms.Count == 0)
        {
            return 0d;
        }

        var words = SnippetBuilder.Tokenize(title);
        if (words.Count == 0)
        {
            return 0d;
        }

        var stems = new string?[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            stems[i] = Stemmable(words[i]) ? SearchStemmer.Stem(words[i]) : null;
        }

        var matched = 0;
        var leads = false;
        foreach (var term in terms)
        {
            var at = FirstMatch(words, stems, term);
            if (at < 0)
            {
                continue;
            }

            matched++;
            leads |= at == 0;
        }

        return matched == 0 ? 0d : ((double)matched / terms.Count) + (leads ? LeadBonus : 0d);
    }

    // Index of the first title word matching the term (prefix or shared stem), or -1.
    private static int FirstMatch(IReadOnlyList<string> words, IReadOnlyList<string?> stems, QueryTerm term)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].StartsWith(term.Token, StringComparison.OrdinalIgnoreCase)
                || (term.Stem is not null && stems[i] is not null && string.Equals(stems[i], term.Stem, StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return -1;
    }

    // Only natural-language words are stemmed, matching what the stems sidecar indexes (see SearchStems).
    private static bool Stemmable(string token)
    {
        if (token.Length < MinStemLength)
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (!char.IsLetter(ch))
            {
                return false;
            }
        }

        return true;
    }
}
