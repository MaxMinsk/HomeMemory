using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-239: a note whose content lives in a structured payload must not lose the query it is TITLED after to a
/// note that mentions it once in passing. FTS5's BM25 normalises every term by the row's TOTAL token count across
/// all indexed columns, so a note with an empty body and a large payload is penalised as "long" even though the
/// match is in its short, high-signal title — on prod the recipe titled after the query sat at lexical rank 17 of
/// 44 while an equipment note that named it once took the top slot.
/// <para>MEMP-237's fixture could not show this, and the follow-up was filed believing a fixture never could. It
/// can: length normalisation is relative, so it only bites when the corpus has a settled average row length AND a
/// spread of relevance to fall through. This fixture supplies both, and reproduces the reported ranking to within
/// one place (titled note 18th of 44 here, 17th of 44 on prod).</para>
/// </summary>
public class TitleLengthNormalisationTests
{
    private const string Query = "chili";

    [Fact]
    public void A_titled_note_with_a_large_payload_outranks_a_passing_mention()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var corpus = SeedFieldReport(repo);

        var page = repo.Search(Query, domain: "kitchen", explain: true, limit: 50);
        var order = Ids(page);

        // Guard the fixture before trusting the assertion: without the lexical deficit there is nothing to fix.
        var titledRank = Explain(page, corpus.Titled).LexicalRank;
        var passingRank = Explain(page, corpus.Passing).LexicalRank;
        Assert.True(titledRank > passingRank + 10,
            $"fixture must reproduce the lexical deficit (titled {titledRank}, passing {passingRank})");

        Assert.True(order.IndexOf(corpus.Titled) < order.IndexOf(corpus.Passing),
            $"the titled note must outrank the passing mention (got {Titles(page)})");
    }

    /// <summary>
    /// The compensation is bounded by how much relevance actually varies in the pool, so a note that is genuinely,
    /// heavily about the query still beats one that merely carries the word in its title and is otherwise
    /// irrelevant. This is the guarantee the fix trades for: near the top of the pool a title match is decisive,
    /// but it cannot overturn a large margin in relevance.
    /// </summary>
    [Fact]
    public void A_title_match_does_not_overturn_a_large_relevance_margin()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var corpus = SeedFieldReport(repo);

        var page = repo.Search(Query, domain: "kitchen", explain: true, limit: 50);
        var order = Ids(page);

        Assert.True(order.IndexOf(corpus.Substantive) < order.IndexOf(corpus.Incidental),
            $"a note about the query must beat a long off-topic note that only has it in the title (got {Titles(page)})");
    }

    /// <summary>
    /// The same fixture under <c>rank=lexical</c>, pinned as a DELIBERATE limit rather than a bug: that mode is
    /// documented as pure BM25, and BM25's length normalisation is the very thing being compensated for. Measured
    /// while choosing the fix — raising the BM25 title column weight instead only reverses this pair at about x20
    /// (x12, the value first proposed, is not enough), and by then a note that merely has the word in its title has
    /// climbed most of the way to a note genuinely about it. The compensation therefore belongs in fusion, where it
    /// can be bounded by the pool, and callers who ask for pure BM25 still get pure BM25.
    /// </summary>
    [Fact]
    public void Pure_lexical_rank_still_reflects_bm25_length_normalisation()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var corpus = SeedFieldReport(repo);

        var lexical = Ids(repo.Search(Query, domain: "kitchen", rank: "lexical", limit: 50));

        Assert.True(lexical.IndexOf(corpus.Passing) < lexical.IndexOf(corpus.Titled),
            "pure BM25 is expected to still favour the shorter row; the hybrid default is what compensates");
    }

    /// <summary>The notes under test within the seeded corpus.</summary>
    private sealed record Corpus(string Titled, string Passing, string Substantive, string Incidental);

    /// <summary>
    /// Rebuilds the field report's corpus shape. Every layer is load-bearing: 300 notes that never mention the
    /// query set the average row length BM25 normalises against AND keep the term rare enough for a positive IDF
    /// (at 44 hits in 66 notes the term is in most of the corpus, IDF goes negative and every score collapses to
    /// zero); 40 notes that mention it once in bodies of increasing length supply the spread of lexical ranks a
    /// real query produces, without which the titled note can only land first or last.
    /// </summary>
    private static Corpus SeedFieldReport(NotesRepository repo)
    {
        for (var i = 0; i < 300; i++)
        {
            repo.Upsert("kitchen", "fact", $"Kitchen note {i}", Prose(60),
                """{ "statement": "filler" }""", null, $"filler-{i}", "tester");
        }

        for (var i = 0; i < 40; i++)
        {
            repo.Upsert("kitchen", "fact", $"Cooking note {i}",
                $"{Prose(8 + (i * 8))} a pinch of {Query} goes in near the end. {Prose(6)}",
                """{ "statement": "passing mention" }""", null, $"mention-{i}", "tester");
        }

        // The pathology: the query is in the TITLE, the body is empty, and the content lives in a bulky payload.
        var titled = repo.Upsert("kitchen", "recipe", $"{Titled(Query)} in a kazan with minced beef",
            body: null, RecipePayload(), null, "chili-in-a-kazan", "tester").Id;

        // The passing mention that took slot #1 on prod: no title match, a short body that names the query.
        var passing = repo.Upsert("kitchen", "fact", "Kazan",
            $"A heavy cast iron pot, good for {Query} and slow stews; {Query} especially. {Prose(12)}",
            """{ "statement": "the pot" }""", null, "kazan", "tester").Id;

        // Squarely about the query: named repeatedly, in a short body — the most relevant note in the corpus.
        var substantive = repo.Upsert("kitchen", "fact", "Peppers to keep in stock",
            $"Dried {Query} peppers, how to store {Query}, when to toast {Query}, and how much {Query} to use.",
            """{ "statement": "the peppers" }""", null, "peppers", "tester").Id;

        // The opposite: the word appears once, in the title, and the note is about something else entirely.
        var incidental = repo.Upsert("kitchen", "fact", $"Guest list for {Titled(Query)} night",
            $"Seating, plates and who is bringing what. {Prose(300)}",
            """{ "statement": "errands" }""", null, "guest-list", "tester").Id;

        return new Corpus(titled, passing, substantive, incidental);
    }

    // A chili recipe genuinely names chili in its steps: the bulk is what buries it, not an absence of the word.
    private static string RecipePayload()
    {
        var ingredients = Enumerable.Range(0, 26)
            .Select(i => new { name = i == 3 ? $"dried {Query} peppers" : $"ingredient {i} {Prose(4)}", amount = "2", unit = "g" });
        return JsonSerializer.Serialize(new
        {
            format = "kazan",
            servings = "5-7",
            occasion = $"a long slow dinner {Prose(16)}",
            ingredients,
            preparation = Enumerable.Range(0, 18).Select(i => i == 2 ? $"toast the {Query} before grinding" : $"prep step {i}: {Prose(10)}"),
            cooking = Enumerable.Range(0, 18).Select(i => $"cook step {i}: {Prose(10)}"),
            result = Prose(20),
        });
    }

    private static List<string> Ids(SearchPage page) => page.Items.Select(item => item.Id).ToList();

    private static string Titles(SearchPage page) => string.Join(" | ", page.Items.Take(6).Select(item => item.Title));

    private static ScoreBreakdown Explain(SearchPage page, string id) =>
        Assert.Single(page.Items, item => item.Id == id).Explain!;

    // Deterministic filler prose with no query term in it; `count` words drawn in a fixed cycle.
    private static string Prose(int count)
    {
        string[] words = ["heat", "the", "pot", "until", "onion", "turns", "soft", "then", "add", "stock", "and", "simmer"];
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(words[i % words.Length]).Append(' ');
        }

        return text.ToString().TrimEnd();
    }

    private static string Titled(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
