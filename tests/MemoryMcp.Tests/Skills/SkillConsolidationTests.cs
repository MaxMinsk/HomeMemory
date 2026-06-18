using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Skills;

public class SkillConsolidationTests
{
    [Fact]
    public void Plan_flags_a_redundant_project_override()
    {
        using var temp = new TempDatabase();
        var skills = NewSkills(temp);
        skills.Upsert("development", "backlog-mgmt", "T", "SAME BODY", "backlog_item", 1, null, "me");                       // general
        skills.Upsert("development", "backlog-mgmt", "T", "SAME BODY", "backlog_item", 1, null, "me", project: "memory-mcp"); // identical override

        var plan = skills.ConsolidationPlan("development");

        Assert.Contains(plan.Items, item => item.Key == "backlog-mgmt" && item.Project == "memory-mcp" && item.Action == "delete");
    }

    [Fact]
    public void Plan_flags_duplicate_bodies_under_different_keys()
    {
        using var temp = new TempDatabase();
        var skills = NewSkills(temp);
        skills.Upsert("development", "alpha", "T", "SHARED TEXT", "backlog_item", 1, null, "me");
        skills.Upsert("development", "beta", "T", "SHARED TEXT", "backlog_item", 1, null, "me");

        var plan = skills.ConsolidationPlan("development");

        Assert.Contains(plan.Items, item => item.Key == "beta" && item.Action == "merge" && item.RelatedKey == "alpha");
        Assert.DoesNotContain(plan.Items, item => item.Key == "alpha"); // the first key is kept as canonical
    }

    [Fact]
    public void Plan_is_empty_when_skills_are_distinct()
    {
        using var temp = new TempDatabase();
        var skills = NewSkills(temp);
        skills.Upsert("development", "one", "T", "body A", "backlog_item", 1, null, "me");
        skills.Upsert("development", "two", "T", "body B", "backlog_item", 1, null, "me");

        Assert.Empty(skills.ConsolidationPlan("development").Items);
    }

    private static SkillsService NewSkills(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new SkillsService(new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()));
    }
}
