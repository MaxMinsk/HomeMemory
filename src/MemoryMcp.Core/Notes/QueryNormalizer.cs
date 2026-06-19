namespace MemoryMcp.Core.Notes;

/// <summary>
/// Normalizes a free-text query for recall (MEMP-191): drops common RU+EN function/stop words so a natural
/// question reduces to its content tokens (e.g. "how many peppers do I have?" -> "peppers"). Punctuation is already
/// removed by <see cref="SnippetBuilder.Tokenize"/>; this only filters the resulting tokens. If every token is a
/// stop word, the originals are kept — recall must never end up searching nothing.
/// </summary>
public static class QueryNormalizer
{
    // Common Russian function words (pronouns, prepositions, question words, particles). Kept as \u escapes so the
    // source stays ASCII (the repo is English-only gated); decoded to real words at type load.
    private const string RussianStopWords =
        "\u0438 \u0432 \u0432\u043e \u043d\u0435 \u0447\u0442\u043e \u043e\u043d \u043d\u0430 \u044f \u0441 \u0441\u043e \u043a\u0430\u043a \u0430 \u0442\u043e \u0432\u0441\u0435 \u043e\u043d\u0430 \u0442\u0430\u043a \u0435\u0433\u043e \u043d\u043e \u0434\u0430 \u0442\u044b \u043a \u0443 \u0436\u0435 \u0432\u044b \u0437\u0430 \u0431\u044b \u043f\u043e \u0435\u0435 \u043c\u043d\u0435 \u0431\u044b\u043b\u043e \u0432\u043e\u0442 \u043e\u0442 \u043c\u0435\u043d\u044f \u0435\u0449\u0435 \u043d\u0435\u0442 \u043e \u0438\u0437 \u0435\u043c\u0443 \u0442\u0435\u043f\u0435\u0440\u044c \u043a\u043e\u0433\u0434\u0430 \u0434\u0430\u0436\u0435 \u043d\u0443 \u0432\u0434\u0440\u0443\u0433 \u043b\u0438 \u0435\u0441\u043b\u0438 \u0443\u0436\u0435 \u0438\u043b\u0438 \u0431\u044b\u0442\u044c \u0431\u044b\u043b \u043d\u0435\u0433\u043e \u0434\u043e \u0432\u0430\u0441 \u043d\u0438\u0431\u0443\u0434\u044c \u043e\u043f\u044f\u0442\u044c \u0443\u0436 \u0432\u0430\u043c \u0432\u0435\u0434\u044c \u0442\u0430\u043c \u043f\u043e\u0442\u043e\u043c \u0441\u0435\u0431\u044f \u043d\u0438\u0447\u0435\u0433\u043e \u0435\u0439 \u043c\u043e\u0436\u0435\u0442 \u043e\u043d\u0438 \u0442\u0443\u0442 \u0433\u0434\u0435 \u0435\u0441\u0442\u044c \u043d\u0430\u0434\u043e \u043d\u0435\u0439 \u0434\u043b\u044f \u043c\u044b \u0442\u0435\u0431\u044f \u0438\u0445 \u0447\u0435\u043c \u0431\u044b\u043b\u0430 \u0441\u0430\u043c \u0447\u0442\u043e\u0431 \u0431\u0435\u0437 \u0431\u0443\u0434\u0442\u043e \u0447\u0435\u0433\u043e \u0440\u0430\u0437 \u0442\u043e\u0436\u0435 \u0441\u0435\u0431\u0435 \u043f\u043e\u0434 \u0431\u0443\u0434\u0435\u0442 \u0436 \u043a\u0442\u043e \u044d\u0442\u043e \u044d\u0442\u043e\u0442 \u0442\u043e\u0433\u043e \u043f\u043e\u0442\u043e\u043c\u0443 \u044d\u0442\u043e\u0433\u043e \u043a\u0430\u043a\u043e\u0439 \u0441\u043e\u0432\u0441\u0435\u043c \u043d\u0438\u043c \u0437\u0434\u0435\u0441\u044c \u044d\u0442\u043e\u043c \u043f\u043e\u0447\u0442\u0438 \u043c\u043e\u0439 \u0442\u0435\u043c \u0447\u0442\u043e\u0431\u044b \u043d\u0435\u0435 \u0441\u0435\u0439\u0447\u0430\u0441 \u0431\u044b\u043b\u0438 \u043a\u0443\u0434\u0430 \u0437\u0430\u0447\u0435\u043c \u0432\u0441\u0435\u0445 \u043d\u0438\u043a\u043e\u0433\u0434\u0430 \u0441\u0435\u0433\u043e\u0434\u043d\u044f \u043c\u043e\u0436\u043d\u043e \u043f\u0440\u0438 \u043e\u0431 \u0445\u043e\u0442\u044c \u043d\u0430\u0434 \u0431\u043e\u043b\u044c\u0448\u0435 \u0442\u043e\u0442 \u0447\u0435\u0440\u0435\u0437 \u044d\u0442\u0438 \u043d\u0430\u0441 \u043f\u0440\u043e \u0432\u0441\u0435\u0433\u043e \u043d\u0438\u0445 \u043a\u0430\u043a\u0430\u044f \u043c\u043d\u043e\u0433\u043e \u0440\u0430\u0437\u0432\u0435 \u0441\u043a\u043e\u043b\u044c\u043a\u043e \u044d\u0442\u0443 \u043c\u043e\u044f \u0441\u0432\u043e\u044e \u044d\u0442\u043e\u0439 \u043f\u0435\u0440\u0435\u0434 \u043b\u0443\u0447\u0448\u0435 \u0447\u0443\u0442\u044c \u0442\u043e\u043c \u0442\u0430\u043a\u043e\u0439 \u0438\u043c \u0431\u043e\u043b\u0435\u0435 \u0432\u0441\u044e";

    private const string EnglishStopWords =
        "the a an of to in is it for on with as at by from how many much do does did i me my we our you your " +
        "what which who whom this that these those are was were be been being have has had and or but if then " +
        "than so no not can could would should will shall may might there here where when why all any some about";

    // Small curated bilingual stop list: the function words that pollute an AND query without carrying meaning.
    private static readonly HashSet<string> StopWords = new(
        RussianStopWords.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(EnglishStopWords.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        StringComparer.Ordinal);

    /// <summary>
    /// Returns the content tokens with stop words removed. If that would leave nothing, the originals are returned
    /// unchanged (so an all-stop-word query still searches something).
    /// </summary>
    /// <param name="tokens">The tokenized query terms (already lower-cased by the tokenizer).</param>
    public static IReadOnlyList<string> StripStopWords(IReadOnlyList<string> tokens)
    {
        var kept = tokens.Where(token => !StopWords.Contains(token)).ToList();
        return kept.Count > 0 ? kept : tokens;
    }

    /// <summary>True when a raw query uses an explicit any-term operator: a standalone <c>OR</c> or a <c>|</c>.</summary>
    /// <param name="query">The raw positive query text.</param>
    public static bool HasOrOperator(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (query.Contains('|', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, "OR", StringComparison.Ordinal)) // uppercase OR is the operator; "or" is just a word
            {
                return true;
            }
        }

        return false;
    }
}
