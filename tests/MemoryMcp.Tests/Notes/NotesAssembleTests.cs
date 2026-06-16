using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesAssembleTests
{
    private const string Payload = """{ "key": "MEMP-301", "status": "ready" }""";

    [Fact]
    public void Assemble_creates_note_and_links_in_one_transaction()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var target = Seed(repo, "MEMP-300");

        var result = repo.Assemble("memory-mcp", "backlog_item", "Case", null, Payload, null, "MEMP-301",
            new[] { new AssembleLink(target, "depends_on") }, "me");

        Assert.True(result.Created);
        Assert.Equal(1, result.LinksCreated);
        Assert.NotNull(repo.Get(result.Id));
        var link = Assert.Single(repo.Links(result.Id));
        Assert.Equal("depends_on", link.Rel);
        Assert.Equal(target, link.NoteId);
    }

    [Fact]
    public void Assemble_rolls_back_entirely_when_a_link_target_is_missing()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        Assert.Throws<AssembleException>(() => repo.Assemble("memory-mcp", "backlog_item", "Case", null, Payload, null, "MEMP-301",
            new[] { new AssembleLink("ghost", "depends_on") }, "me"));

        // Nothing was persisted — the note was not created.
        Assert.Null(repo.GetByDedupKey("memory-mcp", "backlog_item", "MEMP-301"));
    }

    [Fact]
    public void Assemble_rejects_invalid_relation_and_persists_nothing()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var target = Seed(repo, "MEMP-300");

        Assert.Throws<AssembleException>(() => repo.Assemble("memory-mcp", "backlog_item", "Case", null, Payload, null, "MEMP-301",
            new[] { new AssembleLink(target, "Not A Rel") }, "me"));

        Assert.Null(repo.GetByDedupKey("memory-mcp", "backlog_item", "MEMP-301"));
    }

    [Fact]
    public void Assemble_without_links_just_creates_the_note()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var result = repo.Assemble("memory-mcp", "backlog_item", "Solo", null, Payload, null, "MEMP-301", null, "me");

        Assert.Equal(0, result.LinksCreated);
        Assert.NotNull(repo.Get(result.Id));
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string key) =>
        repo.Upsert("memory-mcp", "backlog_item", key, null, $$"""{ "key": "{{key}}", "status": "ready" }""", null, key, "t").Id;
}
