using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class IdentifierNormalizationTests
{
    [Fact]
    public void Upsert_lowercases_domain_type_and_tags()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var id = repo.Upsert("Home", "Backlog_Item", "T", null,
            """{ "key": "MEMP-200", "status": "ready" }""", """["Urgent","KITCHEN","urgent"]""", "MEMP-200", "me").Id;

        var note = repo.Get(id)!;
        Assert.Equal("home", note.Domain);
        Assert.Equal("backlog_item", note.Type);
        Assert.Equal("""["urgent","kitchen"]""", note.TagsJson); // lowercased + de-duped
    }

    [Fact]
    public void Search_and_dedup_are_case_insensitive()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("home", "backlog_item", "T", null,
            """{ "key": "MEMP-200", "status": "ready" }""", """["urgent"]""", "MEMP-200", "me");

        Assert.Single(repo.Search(domain: "HOME").Items);            // domain filter case-insensitive
        Assert.Single(repo.Search(tags: new[] { "URGENT" }).Items);  // tag filter case-insensitive
        Assert.NotNull(repo.GetByDedupKey("Home", "Backlog_Item", "MEMP-200"));

        // A different-cased re-upsert updates the SAME note instead of creating a case-variant duplicate.
        repo.Upsert("HOME", "BACKLOG_ITEM", "T2", null,
            """{ "key": "MEMP-200", "status": "done" }""", null, "MEMP-200", "me");
        Assert.Equal(1, repo.Search(domain: "home").Total);
    }

    [Fact]
    public void Scope_authorization_is_case_insensitive()
    {
        var guard = new ScopeGuard(RequestScope.ForDomains(new[] { "Home", "Kitchen" }));

        Assert.True(guard.IsAllowed("home"));
        Assert.True(guard.IsAllowed("HOME"));
        Assert.True(guard.IsAllowed("kitchen"));
        Assert.False(guard.IsAllowed("work"));
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
