using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-234: a tag discovered through notes_tags/tags_list has to be usable from the recall path. Before this,
// only notes_search took `tags`, so an agent could only put the tag in the query text — where the tokenizer
// splits "feature:mining-rush" into feature/mining/rush and matches it against prose instead of the facet.
public class RecallByTagTests
{
    [Fact]
    public void Recall_by_tag_alone_returns_exactly_the_tagged_notes()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "Panels implementation", "feature:mining-rush");
        Seed(repo, "development", "Timer ownership", "feature:mining-rush");
        Seed(repo, "development", "Unrelated ads note", "area:ads");

        var hits = repo.Recall(query: null, domain: "development", 10, restrictToDomains: null,
            tags: new[] { "feature:mining-rush" }).Hits;

        Assert.Equal(2, hits.Count);
        Assert.DoesNotContain(hits, hit => hit.Title == "Unrelated ads note");
    }

    [Fact]
    public void Recall_requires_every_supplied_tag()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "Both", "feature:mining-rush", "area:client");
        Seed(repo, "development", "One only", "feature:mining-rush");

        var hits = repo.Recall(null, "development", 10, null, tags: new[] { "feature:mining-rush", "area:client" }).Hits;

        Assert.Single(hits);
        Assert.Equal("Both", hits[0].Title);
    }

    [Fact]
    public void Recall_combines_a_tag_filter_with_a_query()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "Timer ownership", "feature:mining-rush");
        Seed(repo, "development", "Panels implementation", "feature:mining-rush");

        var hits = repo.Recall("timer", "development", 10, null, tags: new[] { "feature:mining-rush" }).Hits;

        Assert.Single(hits);
        Assert.Equal("Timer ownership", hits[0].Title);
    }

    [Fact]
    public void Recall_by_tag_still_honors_scope()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "In scope", "feature:mining-rush");
        Seed(repo, "work", "Out of scope", "feature:mining-rush");

        var hits = repo.Recall(null, domain: null, 10, restrictToDomains: new[] { "development" },
            tags: new[] { "feature:mining-rush" }).Hits;

        Assert.Single(hits);
        Assert.Equal("development", hits[0].Domain);
    }

    [Fact]
    public void Memory_context_narrows_its_recall_by_tag()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "Panels implementation", "feature:mining-rush");
        Seed(repo, "development", "Unrelated ads note", "area:ads");
        var assembler = new ContextAssembler(repo, new SkillsService(repo));

        var block = assembler.Assemble(query: null, "development", 10, includeLinks: false, RequestScope.Unrestricted,
            options: new ContextOptions(Tags: new[] { "feature:mining-rush" }));

        Assert.NotNull(block);
        Assert.Single(block!.Recall.Hits);
        Assert.Equal("Panels implementation", block.Recall.Hits[0].Title);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static void Seed(NotesRepository repo, string domain, string title, params string[] tags) =>
        repo.Upsert(domain, "fact", title, title, """{ "statement": "x" }""",
            $"[{string.Join(",", tags.Select(tag => $"\"{tag}\""))}]", title.Replace(' ', '-'), "tester");
}
