using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-218: atomic bulk upsert + links (endpoints by batch dedupKey or existing note id) with project per item.
public class NotesAssembleManyTests
{
    [Fact]
    public void Upserts_items_with_project_and_links_them_by_dedup_key_or_id()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var existing = repo.Upsert("development", "fact", "Evidence", "b", """{ "statement": "x" }""", null, "EX-1", "me").Id;

        var result = repo.AssembleMany(
            new[] { Item("TST-100", "Alpha"), Item("TST-200", "Beta") },
            new[]
            {
                new AssembleManyLink("TST-200", "TST-100", "relates_to"), // new -> new, by dedupKey
                new AssembleManyLink("TST-100", existing, "derived_from"), // new -> existing, by id
            },
            "me");

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, r => Assert.True(r.Created));
        Assert.Equal(2, result.LinksCreated);

        var alpha = result.Items.Single(r => r.DedupKey == "TST-100").Id;
        Assert.Equal("test-proj", repo.Get(alpha)!.Project);                              // project axis set (the gap 218 closes)
        var links = repo.Links(alpha);
        Assert.Contains(links, l => l.Direction == "out" && l.Rel == "derived_from" && l.NoteId == existing);
        Assert.Contains(links, l => l.Direction == "in" && l.Rel == "relates_to");        // Beta -> Alpha
    }

    [Fact]
    public void Aborts_the_whole_batch_on_an_unresolvable_endpoint()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        Assert.Throws<AssembleException>(() => repo.AssembleMany(
            new[] { Item("TST-300", "Gamma") },
            new[] { new AssembleManyLink("TST-300", "NOPE-404", "relates_to") },
            "me"));

        Assert.Null(repo.GetByDedupKey("development", "backlog_item", "TST-300")); // nothing written
    }

    [Fact]
    public void Link_creation_is_idempotent()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var items = new[] { Item("TST-400", "Delta"), Item("TST-500", "Epsilon") };
        var link = new[] { new AssembleManyLink("TST-400", "TST-500", "relates_to") };
        repo.AssembleMany(items, link, "me");

        var again = repo.AssembleMany(items, link, "me");

        Assert.Equal(0, again.LinksCreated);
        Assert.Equal(1, again.LinksAlreadyPresent);
    }

    private static NoteUpsertInput Item(string key, string title) =>
        new("development", "backlog_item", title, title, $$"""{ "key": "{{key}}", "status": "ready" }""", """["backlog"]""", key, "test-proj", null);

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
