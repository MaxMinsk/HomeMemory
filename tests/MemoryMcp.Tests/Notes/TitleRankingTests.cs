using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-237: a note titled after the query must outrank a note that only mentions the query in its body.
/// The fixture reproduces the field report — searching the Russian word for "chili" put a cast-iron-pot note
/// and a soup recipe above both notes with the word in their title, because the title carried no BM25 weight
/// and the equal-weight fusion let type and recency outvote relevance. Cyrillic is assembled from code points
/// so the source stays ASCII (English gate).
/// </summary>
public class TitleRankingTests
{
    private static string Cyr(params int[] codePoints) => new(codePoints.Select(c => (char)c).ToArray());

    // "chili", lower-case and title-case.
    private static readonly string ChiliLower = Cyr(0x0447, 0x0438, 0x043B, 0x0438);
    private static readonly string ChiliTitle = Cyr(0x0427, 0x0438, 0x043B, 0x0438);

    [Fact]
    public void A_title_match_outranks_a_body_only_mention()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // Title-bearing notes: an ordinary type, seeded first, unlinked — every non-text signal is against them.
        var kazanChili = Fact(repo, "chili-in-a-kazan", $"{ChiliTitle} in a kazan with minced beef", "a one pot dinner");
        var driedChili = Fact(repo, "dried-chili", $"Dried {ChiliTitle} peppers in stock", "kept in the pantry");
        // Body-only mentions: a canonical type AND newer — exactly the pool that used to take the top slots.
        var kazan = Recipe(repo, "kazan", "Kazan", $"a cast iron pot, good for {ChiliLower} and stews");
        var soup = Recipe(repo, "tom-yum", "Tom Yum-ish summer soup", $"lemongrass, lime and a little {ChiliLower}");

        var lexical = Ids(repo.Search(ChiliLower, domain: "kitchen", rank: "lexical"));
        var hybrid = Ids(repo.Search(ChiliLower, domain: "kitchen"));

        // The defect was present in BM25 itself and again after fusion, so both orders are asserted.
        foreach (var order in new[] { lexical, hybrid })
        {
            Assert.Equal(4, order.Count);
            Assert.Contains(kazanChili, order.Take(2));
            Assert.Contains(driedChili, order.Take(2));
            Assert.True(order.IndexOf(kazanChili) < order.IndexOf(kazan), "a titled note must beat the pot note");
            Assert.True(order.IndexOf(driedChili) < order.IndexOf(soup), "a titled note must beat the soup note");
        }
    }

    [Fact]
    public void A_partial_title_match_is_its_own_ranking_signal()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // The title CONTAINS the query but is not equal to it, so the exact-title tier (MEMP-160) never fires.
        var titled = Fact(repo, "dried-chili", $"Dried {ChiliTitle} peppers in stock", "kept in the pantry");
        var bodyOnly = Fact(repo, "kazan", "Kazan", $"a cast iron pot, good for {ChiliLower} and stews");

        var items = repo.Search(ChiliLower, domain: "kitchen", explain: true).Items;

        Assert.Equal(1, Assert.Single(items, item => item.Id == titled).Explain!.TitleRank);
        Assert.Equal(2, Assert.Single(items, item => item.Id == bodyOnly).Explain!.TitleRank);
    }

    [Fact]
    public void Title_signal_is_neutral_when_no_title_matches()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Fact(repo, "kazan", "Kazan", $"a cast iron pot, good for {ChiliLower} and stews");
        Fact(repo, "pantry", "Pantry", $"a shelf of dried {ChiliLower} and spices");

        var items = repo.Search(ChiliLower, domain: "kitchen", explain: true).Items;

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(1, item.Explain!.TitleRank)); // all tie at rank 1 => no effect
    }

    private static List<string> Ids(SearchPage page) => page.Items.Select(item => item.Id).ToList();

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    // fact: an ordinary type (type goodness 1).
    private static string Fact(NotesRepository repo, string key, string title, string body) =>
        repo.Upsert("kitchen", "fact", title, body, """{ "statement": "a kitchen note" }""", null, key, "tester").Id;

    // recipe: a canonical type (type goodness 2) — the signal that used to outvote relevance.
    private static string Recipe(NotesRepository repo, string key, string title, string body) =>
        repo.Upsert("kitchen", "recipe", title, body, """{ "format": "stove" }""", null, key, "tester").Id;
}
