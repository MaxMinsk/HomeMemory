using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-174/175/176/177: hybrid RRF recall ranking, importance/pin boost, budget packing, score-breakdown explain.
public class NotesHybridRankingTests
{
    // MEMP-237 changed the contract this pair of tests describes. Equal weights let two contextual signals
    // outvote relevance, so a fresher, better-connected note displaced the stronger text match; relevance now
    // leads and the contextual signals order what relevance leaves tied.
    [Fact]
    public void Hybrid_keeps_the_stronger_text_match_above_a_fresher_better_linked_note()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // Orphan is seeded first (oldest) and is the strongest pure-lexical match for "alpha".
        var orphan = Seed(repo, "MEMP-200", title: "alpha", body: "alpha alpha alpha");
        var n1 = Seed(repo, "MEMP-210", title: "gamma", body: "gamma");   // non-matching neighbors
        var n2 = Seed(repo, "MEMP-211", title: "delta", body: "delta");
        var fresh = Seed(repo, "MEMP-201", title: "alpha", body: "alpha"); // newest, and well-linked
        repo.Link(fresh, n1, "relates_to");
        repo.Link(fresh, n2, "relates_to");

        // Pure lexical: the orphan (more "alpha") wins.
        var lexical = repo.Search("alpha", domain: "memory-mcp", rank: "lexical");
        Assert.Equal(orphan, lexical.Items[0].Id);

        // Hybrid: recency and link-degree no longer overturn a better text match on their own.
        var hybrid = repo.Search("alpha", domain: "memory-mcp", rank: "hybrid");
        Assert.Equal(orphan, hybrid.Items[0].Id);
    }

    [Fact]
    public void Nontext_signals_order_hits_that_tie_on_relevance()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // Identical text, so BM25 and the title signal tie and only the contextual signals can separate them.
        var orphan = Seed(repo, "MEMP-200", title: "alpha", body: "alpha");
        var n1 = Seed(repo, "MEMP-210", title: "gamma", body: "gamma");
        var fresh = Seed(repo, "MEMP-201", title: "alpha", body: "alpha"); // newest, and linked
        repo.Link(fresh, n1, "relates_to");

        var hybrid = repo.Search("alpha", domain: "memory-mcp", rank: "hybrid", explain: true);
        Assert.Equal(fresh, hybrid.Items[0].Id);
        Assert.Equal(orphan, hybrid.Items[1].Id);
        Assert.All(hybrid.Items, item => Assert.Equal(1, item.Explain!.LexicalRank)); // relevance really is a tie
    }

    [Fact]
    public void Lexical_rank_is_pure_bm25_order()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var strong = Seed(repo, "MEMP-200", title: "alpha", body: "alpha alpha alpha");
        var weak = Seed(repo, "MEMP-201", title: "alpha", body: "alpha");

        // rank=lexical opts out of the (now default) hybrid blend => pure BM25 order.
        var items = repo.Search("alpha", domain: "memory-mcp", rank: "lexical").Items;
        Assert.Equal(strong, items[0].Id);
        Assert.Equal(weak, items[1].Id);
    }

    [Fact]
    public void A_pinned_tag_lifts_the_importance_signal()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var plain = Seed(repo, "MEMP-200", title: "alpha", body: "alpha");
        var pinned = Seed(repo, "MEMP-201", title: "alpha", body: "alpha", tags: """["pinned"]""");

        var items = repo.Search("alpha", domain: "memory-mcp", rank: "hybrid", explain: true).Items;
        var pinnedHit = Assert.Single(items, item => item.Id == pinned);
        var plainHit = Assert.Single(items, item => item.Id == plain);

        Assert.NotNull(pinnedHit.Explain);
        Assert.Equal(1, pinnedHit.Explain!.ImportanceRank); // pinned note ranks first on importance
        Assert.Equal(2, plainHit.Explain!.ImportanceRank);  // the unpinned note trails it
    }

    [Fact]
    public void Importance_signal_is_neutral_when_nothing_is_pinned()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-200", title: "alpha", body: "alpha");
        Seed(repo, "MEMP-201", title: "alpha", body: "alpha");

        var items = repo.Search("alpha", domain: "memory-mcp", rank: "hybrid", explain: true).Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(1, item.Explain!.ImportanceRank)); // all tie at rank 1 => no effect
    }

    [Fact]
    public void Explain_is_off_by_default()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-200", title: "alpha", body: "alpha");

        var hybrid = repo.Search("alpha", domain: "memory-mcp", rank: "hybrid").Items[0];
        Assert.Null(hybrid.Explain);
    }

    [Fact]
    public void Recall_budget_packs_hits_within_the_char_budget()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        for (var i = 0; i < 5; i++)
        {
            Seed(repo, $"MEMP-2{i:00}", title: "alpha", body: $"alpha filler context number {i}");
        }

        var recall = repo.Recall("alpha", "memory-mcp", 10, restrictToDomains: null, includeLinks: false, maxHops: 1, budgetChars: 1);

        Assert.Equal(1, recall.BudgetChars);
        Assert.True(recall.UsedChars <= 1, $"used {recall.UsedChars} exceeds budget");
        Assert.Single(recall.Hits);                              // only one (trimmed) snippet fits a 1-char budget
        Assert.Equal(1, recall.Hits[0].Snippet!.Length);         // the last slot is trimmed to fit
        Assert.True(recall.DroppedCount >= 4, $"dropped {recall.DroppedCount}"); // the rest are reported as dropped
    }

    [Fact]
    public void Recall_without_a_budget_pages_by_count_as_before()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        for (var i = 0; i < 3; i++)
        {
            Seed(repo, $"MEMP-2{i:00}", title: "alpha", body: $"alpha body {i}");
        }

        var recall = repo.Recall("alpha", "memory-mcp", 2, restrictToDomains: null);

        Assert.Equal(2, recall.Hits.Count);
        Assert.Null(recall.BudgetChars);
        Assert.Null(recall.UsedChars);
        Assert.Null(recall.DroppedCount);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string key, string title, string body, string? tags = null)
    {
        var payload = $$"""{ "key": "{{key}}", "status": "ready" }""";
        return repo.Upsert("memory-mcp", "backlog_item", title, body, payload, tags, key, "tester").Id;
    }
}
