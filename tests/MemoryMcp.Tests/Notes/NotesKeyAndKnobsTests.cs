using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-220 (next_key), MEMP-225 (exact-key search), MEMP-223 (recall type filter + noRelax).
public class NotesKeyAndKnobsTests
{
    [Fact]
    public void NextKey_returns_one_past_the_highest_suffix_for_the_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "TRD-100", "binance-maf-trader");
        Seed(repo, "TRD-130", "binance-maf-trader");
        Seed(repo, "MEMP-214", "memory-mcp"); // other project, ignored

        var next = repo.NextKey("binance-maf-trader", "TRD", restrictToDomains: null);

        Assert.Equal("TRD-131", next.NextKey);
        Assert.Equal(130, next.CurrentMax);
        Assert.Equal(2, next.MatchedKeys);
    }

    [Fact]
    public void NextKey_starts_at_001_when_the_project_has_no_keys()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var next = repo.NextKey("fresh-proj", "ABC", restrictToDomains: null);

        Assert.Equal("ABC-001", next.NextKey);
        Assert.Null(next.CurrentMax);
    }

    [Fact]
    public void Search_for_a_ticket_key_returns_only_that_note()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "TRD-131", "binance-maf-trader");
        repo.Upsert("development", "fact", "Mentions TRD-131", "see TRD-131 for the entry contract", """{ "statement": "x" }""", null, "distractor", "me");

        var page = repo.Search("TRD-131", domain: "development");

        Assert.Equal(1, page.Total);
        Assert.Equal("TRD-131", Assert.Single(page.Items).Title); // exact dedup match, not the body mention
    }

    [Fact]
    public void Recall_can_filter_hits_by_type()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "fact", "Alpha fact", "alpha gizmo", """{ "statement": "x" }""", null, "alpha-fact", "me");
        Seed(repo, "TST-100", "test-proj", "alpha gizmo");

        var hits = repo.Recall("alpha", "development", 10, restrictToDomains: null, types: new[] { "fact" }).Hits;

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("fact", h.Type)); // backlog_item excluded
    }

    [Fact]
    public void Recall_noRelax_does_not_widen_a_precise_query()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "fact", "Alpha", "alpha content only", """{ "statement": "x" }""", null, "alpha", "me");

        var auto = repo.Recall("alpha zzzznomatch", "development", 10, restrictToDomains: null);        // AND->any auto-relax
        var strict = repo.Recall("alpha zzzznomatch", "development", 10, restrictToDomains: null, noRelax: true);

        Assert.True(auto.Relaxed);
        Assert.NotEmpty(auto.Hits);      // relaxed to any-term -> finds the alpha note
        Assert.Empty(strict.Hits);       // strict AND -> no note has both tokens
    }

    private static void Seed(NotesRepository repo, string key, string project, string body = "task body") =>
        repo.Upsert("development", "backlog_item", key, body, $$"""{ "key": "{{key}}", "status": "ready" }""", """["backlog"]""", key, "me", project);

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
