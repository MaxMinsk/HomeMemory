using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class ContextAssemblerTests
{
    [Fact]
    public void Assemble_layers_rules_skills_and_recall_for_a_domain()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "memory_rule", "Baseline", null, """{ "description": "always", "always_apply": true, "priority": 1 }""", null, "rule-base", "me");
        repo.Upsert("kitchen", "memory_rule", "High", null, """{ "description": "hi", "priority": 9 }""", null, "rule-high", "me");
        repo.Upsert("kitchen", "memory_rule", "Gone", null, """{ "description": "old", "status": "deprecated" }""", null, "rule-dep", "me");
        repo.Upsert("kitchen", "fact", "Borscht", "beetroot soup", """{ "statement": "borscht is beetroot soup" }""", null, "borscht", "me");
        skills.Upsert("kitchen", "recipe-authoring", "Recipe authoring", "write recipes consistently", "recipe", 1, null, "me");

        var block = new ContextAssembler(repo, skills).Assemble("borscht", "kitchen", 10, includeLinks: true, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Equal("kitchen", block!.Domain);
        Assert.Equal(2, block.Rules.Count);              // deprecated rule excluded
        Assert.Equal("Baseline", block.Rules[0].Title);  // always_apply ranks above higher priority
        Assert.Contains(block.Skills, s => s.Key == "recipe-authoring");
        Assert.Contains(block.Recall.Hits, h => h.Title == "Borscht");
        Assert.False(string.IsNullOrEmpty(block.Policy));
    }

    [Fact]
    public void Assemble_returns_null_when_domain_out_of_scope()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);

        var block = new ContextAssembler(repo, skills).Assemble("x", "kitchen", 5, includeLinks: false, RequestScope.ForDomains(new[] { "home" }));

        Assert.Null(block);
    }

    [Fact]
    public void Assemble_warns_about_stale_rules()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "memory_rule", "Aging", null,
            """{ "description": "x", "stale_after_days": 30, "last_verified_at": "2026-01-01" }""", null, "rule-aging", "me");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero));

        var block = new ContextAssembler(repo, skills, clock).Assemble("x", "kitchen", 5, includeLinks: false, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Contains(block!.Warnings, w => w.Contains("outdated", StringComparison.OrdinalIgnoreCase));
    }

    private static (NotesRepository Repo, SkillsService Skills) New(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        return (repo, new SkillsService(repo));
    }
}
