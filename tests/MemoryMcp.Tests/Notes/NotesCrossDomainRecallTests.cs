using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-213: with no domain, recall spans every authorized domain, and a domain-diverse pass keeps one big
// domain from drowning the smaller ones.
public class NotesCrossDomainRecallTests
{
    [Fact]
    public void Recall_without_a_domain_spans_all_domains()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "alpha in dev");
        Seed(repo, "kitchen", "alpha in kitchen");

        var hits = repo.Recall("alpha", domain: null, 10, restrictToDomains: null).Hits;

        Assert.Contains(hits, h => h.Domain == "development");
        Assert.Contains(hits, h => h.Domain == "kitchen");
    }

    [Fact]
    public void Diverse_recall_surfaces_a_small_domain_over_a_dominant_one()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // A dominant domain with several matches, and one match in a small domain.
        Seed(repo, "development", "alpha one");
        Seed(repo, "development", "alpha two");
        Seed(repo, "development", "alpha three");
        Seed(repo, "kitchen", "alpha solo"); // newest, but one lone domain

        var diverse = repo.Recall("alpha", domain: null, 2, restrictToDomains: null, diverseByDomain: true).Hits;

        Assert.Equal(2, diverse.Count);
        Assert.Contains(diverse, h => h.Domain == "development"); // breadth: both domains represented in the top 2
        Assert.Contains(diverse, h => h.Domain == "kitchen");
    }

    [Fact]
    public void Recall_without_a_domain_respects_scope()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "alpha in dev");
        Seed(repo, "kitchen", "alpha in kitchen");

        var hits = repo.Recall("alpha", domain: null, 10, restrictToDomains: new[] { "kitchen" }).Hits;

        Assert.All(hits, h => Assert.Equal("kitchen", h.Domain)); // development is outside scope
        Assert.NotEmpty(hits);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static void Seed(NotesRepository repo, string domain, string title) =>
        repo.Upsert(domain, "fact", title, title, """{ "statement": "x" }""", null, title.Replace(' ', '-'), "tester");
}
