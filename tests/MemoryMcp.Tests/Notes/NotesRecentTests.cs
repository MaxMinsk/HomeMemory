using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesRecentTests
{
    [Fact]
    public void Recent_returns_most_recently_updated_first()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero));
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources(), clock);
        var a = Seed(repo, "MEMP-200");
        clock.Advance(TimeSpan.FromMinutes(1));
        var b = Seed(repo, "MEMP-201");

        var recent = repo.Recent("memory-mcp", null, 10, restrictToDomains: null, byUsage: false);

        Assert.Equal(b, recent[0].Id); // newest first
        Assert.Equal(a, recent[1].Id);
    }

    [Fact]
    public void Recent_by_usage_ranks_most_accessed_first()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        var a = Seed(repo, "MEMP-200");
        var b = Seed(repo, "MEMP-201");
        var usage = new UsageStore(factory);
        usage.Record(a);
        usage.Record(a); // a accessed twice
        usage.Record(b); // b once

        var recent = repo.Recent("memory-mcp", null, 10, restrictToDomains: null, byUsage: true);

        Assert.Equal(a, recent[0].Id);
        Assert.Equal(b, recent[1].Id);
    }

    [Fact]
    public void Recent_respects_scope()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-200", "status": "ready" }""", null, "MEMP-200", "t");
        repo.Upsert("work", "backlog_item", "W", null, """{ "key": "WORK-200", "status": "ready" }""", null, "WORK-200", "t");

        var recent = repo.Recent(null, null, 10, restrictToDomains: new[] { "memory-mcp" }, byUsage: false);

        Assert.All(recent, r => Assert.Equal("memory-mcp", r.Domain));
        Assert.Single(recent);
    }

    private static string Seed(NotesRepository repo, string key) =>
        repo.Upsert("memory-mcp", "backlog_item", key, null, $$"""{ "key": "{{key}}", "status": "ready" }""", null, key, "t").Id;
}
