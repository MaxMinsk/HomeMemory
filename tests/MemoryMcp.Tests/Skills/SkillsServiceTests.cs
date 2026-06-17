using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Skills;

public class SkillsServiceTests
{
    [Fact]
    public void Upsert_then_get_round_trips_full_skill()
    {
        using var temp = new TempDatabase();
        var skills = NewService(temp);

        skills.Upsert("memory-mcp", "recipe-authoring", "Recipe authoring",
            "Use grams; one step per line.", "recipe", 1, "How to write recipes", "tester");

        var got = skills.Get("memory-mcp", "recipe-authoring");
        Assert.NotNull(got);
        Assert.Equal("recipe-authoring", got!.Key);
        Assert.Equal("recipe", got.TargetType);
        Assert.Equal(1, got.Version);
        Assert.Equal("How to write recipes", got.Summary);
        Assert.Contains("grams", got.Body!);
    }

    [Fact]
    public void List_returns_metadata_without_body_and_filters_by_target_type()
    {
        using var temp = new TempDatabase();
        var skills = NewService(temp);
        skills.Upsert("memory-mcp", "recipe-authoring", "R", "body-r", "recipe", 1, null, "t");
        skills.Upsert("memory-mcp", "backlog-mgmt", "B", "body-b", "backlog_item", 1, null, "t");

        var all = skills.List("memory-mcp");
        Assert.Equal(2, all.Count);
        Assert.All(all, skill => Assert.Null(skill.Body)); // list omits the body

        var recipeOnly = skills.List("memory-mcp", "recipe");
        Assert.Equal("recipe-authoring", Assert.Single(recipeOnly).Key);
    }

    [Fact]
    public void Upsert_is_idempotent_and_bumps_version_in_place()
    {
        using var temp = new TempDatabase();
        var skills = NewService(temp);

        skills.Upsert("memory-mcp", "recipe-authoring", "R", "v1 body", "recipe", 1, null, "t");
        skills.Upsert("memory-mcp", "recipe-authoring", "R", "v2 body", "recipe", 2, null, "t");

        Assert.Single(skills.List("memory-mcp"));
        var got = skills.Get("memory-mcp", "recipe-authoring");
        Assert.Equal(2, got!.Version);
        Assert.Equal("v2 body", got.Body);
    }

    [Fact]
    public void Get_missing_returns_null()
    {
        using var temp = new TempDatabase();
        Assert.Null(NewService(temp).Get("memory-mcp", "nope"));
    }

    [Fact]
    public void Upsert_rejects_invalid_key_against_schema()
    {
        using var temp = new TempDatabase();
        var skills = NewService(temp);

        Assert.Throws<NoteValidationException>(() =>
            skills.Upsert("memory-mcp", "Bad_Key", "X", "body", "recipe", 1, null, "t"));
    }

    [Fact]
    public void Project_skill_overrides_the_domain_general_one()
    {
        using var temp = new TempDatabase();
        var skills = NewService(temp);
        skills.Upsert("development", "backlog-management", "General", "general body", null, 1, null, "me");
        skills.Upsert("development", "backlog-management", "Unity", "unity body", null, 1, null, "me", project: "unity-solitaire");
        skills.Upsert("development", "release", "Other", "x", null, 1, null, "me", project: "other-proj");

        // Get: project wins, falls back to general.
        Assert.Equal("Unity", skills.Get("development", "backlog-management", "unity-solitaire")!.Title);
        Assert.Equal("unity-solitaire", skills.Get("development", "backlog-management", "unity-solitaire")!.Project);
        Assert.Equal("General", skills.Get("development", "backlog-management")!.Title);

        // List for the project: one entry per key (the override); another project's skill is not surfaced.
        var listed = skills.List("development", null, "unity-solitaire");
        Assert.Equal("Unity", Assert.Single(listed, s => s.Key == "backlog-management").Title);
        Assert.DoesNotContain(listed, s => s.Key == "release");
    }

    private static SkillsService NewService(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new SkillsService(new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()));
    }
}
