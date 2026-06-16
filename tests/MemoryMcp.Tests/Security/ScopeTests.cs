using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Security;

public class ScopeTests
{
    [Fact]
    public void Unrestricted_scope_allows_everything()
    {
        var guard = new ScopeGuard(RequestScope.Unrestricted);

        Assert.True(guard.IsAllowed("anything"));
        guard.Authorize("work"); // does not throw
        Assert.Null(guard.RestrictionForSearch(null));
        Assert.Null(guard.RestrictionForSearch("work"));
    }

    [Fact]
    public void Restricted_scope_enforces_allowed_domains()
    {
        var guard = new ScopeGuard(RequestScope.ForDomains(new[] { "kitchen" }));

        Assert.True(guard.IsAllowed("kitchen"));
        Assert.False(guard.IsAllowed("work"));
        Assert.Throws<ScopeForbiddenException>(() => guard.Authorize("work"));
    }

    [Fact]
    public void RestrictionForSearch_handles_explicit_and_implicit_domain()
    {
        var guard = new ScopeGuard(RequestScope.ForDomains(new[] { "kitchen" }));

        Assert.Null(guard.RestrictionForSearch("kitchen"));                 // in-scope explicit -> no extra restriction
        Assert.Null(guard.RestrictionForSearch("commons"));                 // commons is readable by everyone
        Assert.Throws<ScopeForbiddenException>(() => guard.RestrictionForSearch("work"));
        var implicitRestriction = guard.RestrictionForSearch(null);        // no filter -> allowed set PLUS commons
        Assert.NotNull(implicitRestriction);
        Assert.Equal(new[] { "kitchen", "commons" }, implicitRestriction!);
    }

    [Fact]
    public void Commons_domain_is_readable_by_all_but_writable_only_in_scope()
    {
        var guard = new ScopeGuard(RequestScope.ForDomains(new[] { "kitchen" }));

        Assert.True(guard.IsAllowed("commons"));   // read: allowed for any scope
        Assert.True(guard.IsAllowed("Commons"));   // case-insensitive
        Assert.Throws<ScopeForbiddenException>(() => guard.Authorize("commons")); // write: not in scope -> denied
        Assert.NotNull(guard.WriteRestriction());
        Assert.DoesNotContain("commons", guard.WriteRestriction()!);              // commons excluded from write scope
        Assert.Contains("kitchen", guard.WriteRestriction()!);

        var root = new ScopeGuard(RequestScope.Unrestricted);
        root.Authorize("commons");                 // root may write commons (no throw)
        Assert.Null(root.WriteRestriction());      // unrestricted -> no write restriction
    }

    [Fact]
    public void Search_restrict_to_domains_filters_results()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        Seed(repo, "kitchen", "KIT-100");
        Seed(repo, "work", "WORK-100");

        Assert.Single(repo.Search(restrictToDomains: new[] { "kitchen" }).Items);
        Assert.Equal(2, repo.Search().Total);
        Assert.Empty(repo.Search(restrictToDomains: Array.Empty<string>()).Items);
    }

    private static void Seed(NotesRepository repo, string domain, string key) =>
        repo.Upsert(domain, "backlog_item", key, null,
            $$"""{ "key": "{{key}}", "status": "ready" }""", null, key, "tester");
}
