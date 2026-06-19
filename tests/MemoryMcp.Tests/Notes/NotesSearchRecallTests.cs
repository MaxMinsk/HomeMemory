using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-190/191/193/194: any-term + auto AND→OR fallback, stop-word stripping, default hybrid ranking, OR operator.
public class NotesSearchRecallTests
{
    [Fact]
    public void Stop_words_let_a_natural_question_match()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-201", "Peppers", "I grow many peppers in the greenhouse");

        // Strict AND of all words would fail (no note has how/many/do/i/have); stop-word stripping leaves "peppers".
        var page = repo.Search("how many peppers do i have", domain: "memory-mcp", match: "all");

        Assert.Single(page.Items);
        Assert.False(page.Relaxed); // AND still matched (only content token "peppers" remained)
    }

    [Fact]
    public void Auto_falls_back_to_ranked_any_when_AND_finds_nothing()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-201", "Alpha", "alpha content here");

        var strict = repo.Search("alpha zulu", domain: "memory-mcp", match: "all");
        Assert.Empty(strict.Items);          // no note has both alpha AND zulu
        Assert.False(strict.Relaxed);

        var auto = repo.Search("alpha zulu", domain: "memory-mcp"); // default = auto
        Assert.Single(auto.Items);           // widened to any-term, returns the alpha note
        Assert.True(auto.Relaxed);

        var any = repo.Search("alpha zulu", domain: "memory-mcp", match: "any");
        Assert.Single(any.Items);
        Assert.False(any.Relaxed);           // 'any' from the start is not a fallback
    }

    [Fact]
    public void Or_operator_forces_any_term()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-201", "Alpha", "alpha content");

        var page = repo.Search("alpha OR zulu", domain: "memory-mcp");
        Assert.Single(page.Items); // OR makes it any-term: the alpha note matches
    }

    [Fact]
    public void Default_rank_is_hybrid_and_lexical_is_opt_out()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var strong = Seed(repo, "MEMP-201", "alpha", "alpha alpha alpha");
        var weak = Seed(repo, "MEMP-202", "alpha", "alpha");

        // Pure BM25 (opt-out): the term-dense note wins outright.
        var lexical = repo.Search("alpha", domain: "memory-mcp", rank: "lexical").Items;
        Assert.Equal(strong, lexical[0].Id);
        Assert.Equal(weak, lexical[1].Id);

        // Default hybrid still returns both, with the relevance breakdown available on request.
        var hybrid = repo.Search("alpha", domain: "memory-mcp", explain: true).Items;
        Assert.Equal(2, hybrid.Count);
        Assert.All(hybrid, h => Assert.NotNull(h.Explain)); // hybrid is the default (no rank arg)
    }

    [Fact]
    public void Type_weight_ranks_a_canonical_type_above_an_ordinary_one()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var decision = repo.Upsert("memory-mcp", "decision", "alpha decision", "alpha body",
            """{ "decision": "use alpha" }""", null, "DEC-1", "t").Id;
        var item = repo.Upsert("memory-mcp", "backlog_item", "alpha item", "alpha body",
            """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t").Id;

        var hits = repo.Search("alpha", domain: "memory-mcp", explain: true).Items;
        var dec = Assert.Single(hits, h => h.Id == decision);
        var bk = Assert.Single(hits, h => h.Id == item);

        Assert.Equal(1, dec.Explain!.TypeRank); // decision is a canonical type → strongest type signal
        Assert.Equal(2, bk.Explain!.TypeRank);  // backlog_item is ordinary → trails it
    }

    [Fact]
    public void Recall_widens_to_any_term_for_a_natural_question()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-201", "Peppers", "alpha peppers grow here");

        // A question-form recall whose tokens don't all co-occur used to return 0; now it widens + ranks.
        var recall = repo.Recall("where are my alpha zulu peppers", "memory-mcp", 10, restrictToDomains: null, includeLinks: false);

        Assert.NotEmpty(recall.Hits);
        Assert.True(recall.Relaxed);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string key, string title, string body) =>
        repo.Upsert("memory-mcp", "backlog_item", title, body, $$"""{ "key": "{{key}}", "status": "ready" }""", null, key, "t").Id;
}
