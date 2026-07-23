using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-212: when a project name is passed where a domain is expected (e.g. domain='unity-solitaire'),
// resolve it to the real domain + project instead of returning an empty result.
public class NotesProjectAsDomainTests
{
    [Fact]
    public void Resolve_returns_null_for_a_real_domain()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "MEMP-100", "alpha", "memory-mcp");

        Assert.Null(repo.ResolveProjectAsDomain("development", restrictToDomains: null)); // a working call is never re-resolved
    }

    [Fact]
    public void Resolve_maps_a_project_name_to_its_domain()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "US-100", "gameplay", "unity-solitaire");
        Seed(repo, "development", "US-200", "levels", "unity-solitaire");

        var resolved = repo.ResolveProjectAsDomain("unity-solitaire", restrictToDomains: null);

        Assert.NotNull(resolved);
        Assert.Equal("development", resolved!.Domain);
        Assert.Equal("unity-solitaire", resolved.Project);
        Assert.Equal(2, resolved.NoteCount);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "US-100", "gameplay", "unity-solitaire");

        var resolved = repo.ResolveProjectAsDomain("Unity-Solitaire", restrictToDomains: null);

        Assert.NotNull(resolved);
        Assert.Equal("development", resolved!.Domain);
    }

    [Fact]
    public void Resolve_returns_null_for_an_unknown_value()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "US-100", "gameplay", "unity-solitaire");

        Assert.Null(repo.ResolveProjectAsDomain("no-such-thing", restrictToDomains: null));
    }

    [Fact]
    public void Resolve_picks_the_domain_holding_the_most_of_the_projects_notes()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "SH-100", "a", "shared");
        Seed(repo, "development", "SH-200", "b", "shared");
        Seed(repo, "home", "SH-300", "c", "shared");

        var resolved = repo.ResolveProjectAsDomain("shared", restrictToDomains: null);

        Assert.Equal("development", resolved!.Domain); // 2 notes beats home's 1
        Assert.Equal(2, resolved.NoteCount);
    }

    [Fact]
    public void Resolve_respects_scope_restriction()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "development", "US-100", "gameplay", "unity-solitaire");

        // Caller may only read 'home' — the project lives in 'development', so it must not leak.
        Assert.Null(repo.ResolveProjectAsDomain("unity-solitaire", restrictToDomains: new[] { "home" }));
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static void Seed(NotesRepository repo, string domain, string key, string title, string project)
    {
        var payload = $$"""{ "key": "{{key}}", "status": "ready" }""";
        repo.Upsert(domain, "backlog_item", title, title, payload, null, key, "tester", project);
    }
}
