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
        skills.Upsert("kitchen", "recipe-authoring", "Recipe authoring",
            "Writes recipes consistently. Use when authoring or editing a recipe.", "recipe", 1, null, "me");

        var block = new ContextAssembler(repo, skills).Assemble(
            "authoring a borscht recipe", "kitchen", 10, includeLinks: true, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Equal("kitchen", block!.Domain);
        Assert.Equal(2, block.Rules.Count);              // deprecated rule excluded
        Assert.Equal("Baseline", block.Rules[0].Title);  // always_apply ranks above higher priority
        // Skills are now SELECTED against the task rather than listed (MEMP-257), so this asserts the layer is
        // present for a query that genuinely calls for it.
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

    [Fact]
    public void Assemble_includes_project_and_general_rules_but_not_other_projects()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("development", "memory_rule", "General", null, """{ "description": "g" }""", null, "rule-g", "me");
        repo.Upsert("development", "memory_rule", "Unity", null, """{ "description": "u" }""", null, "rule-u", "me", project: "unity-solitaire");
        repo.Upsert("development", "memory_rule", "Other", null, """{ "description": "o" }""", null, "rule-o", "me", project: "other");

        var block = new ContextAssembler(repo, skills).Assemble("x", "development", 5, includeLinks: false, RequestScope.Unrestricted, project: "unity-solitaire");

        Assert.NotNull(block);
        var titles = block!.Rules.Select(r => r.Title).ToList();
        Assert.Contains("Unity", titles);       // project-specific
        Assert.Contains("General", titles);     // domain-general (project null)
        Assert.DoesNotContain("Other", titles); // another project's rule is excluded
    }

    [Fact]
    public void Assemble_warns_about_a_stale_project_state()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("development", "project_state", "memory-mcp state", null,
            """{ "project": "memory-mcp", "state": "v0.56.2 in prod", "updated": "2026-01-01" }""", null, "state-memory-mcp", "me", project: "memory-mcp");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)); // ~150d after 'updated'

        var block = new ContextAssembler(repo, skills, clock)
            .Assemble("x", "development", 5, includeLinks: false, RequestScope.Unrestricted, project: "memory-mcp");

        Assert.NotNull(block);
        Assert.Contains(block!.Warnings, w => w.Contains("may be stale", StringComparison.Ordinal) && w.Contains("refresh", StringComparison.Ordinal));
    }

    [Fact]
    public void Assemble_does_not_warn_about_a_fresh_project_state()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        repo.Upsert("development", "project_state", "memory-mcp state", null,
            """{ "project": "memory-mcp", "state": "current", "updated": "2026-05-31" }""", null, "state-memory-mcp", "me", project: "memory-mcp");

        var block = new ContextAssembler(repo, skills, clock)
            .Assemble("x", "development", 5, includeLinks: false, RequestScope.Unrestricted, project: "memory-mcp");

        Assert.NotNull(block);
        Assert.DoesNotContain(block!.Warnings, w => w.Contains("may be stale", StringComparison.Ordinal));
    }

    [Fact]
    public void Assemble_resolves_a_project_name_passed_as_domain()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("development", "memory_rule", "US rule", null, """{ "description": "u" }""", null, "rule-us", "me", project: "unity-solitaire");
        repo.Upsert("development", "backlog_item", "Card hint", "cards hint animation", """{ "key": "US-100", "status": "ready" }""", null, "US-100", "me", project: "unity-solitaire");

        // Caller mistakenly passed the PROJECT name where a domain is expected.
        var block = new ContextAssembler(repo, skills).Assemble("cards hint", "unity-solitaire", 10, includeLinks: false, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Equal("development", block!.Domain);                                            // resolved to the real domain
        Assert.Contains(block.Warnings, w => w.Contains("is a project", StringComparison.Ordinal) && w.Contains("unity-solitaire", StringComparison.Ordinal));
        Assert.Contains(block.Rules, r => r.Title == "US rule");                               // project rules loaded
        Assert.Contains(block.Recall.Hits, h => h.Title == "Card hint");                       // project notes recalled
    }

    [Fact]
    public void Assemble_does_not_resolve_a_real_domain()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "fact", "Borscht", "beetroot soup", """{ "statement": "x" }""", null, "borscht", "me");

        var block = new ContextAssembler(repo, skills).Assemble("borscht", "kitchen", 10, includeLinks: false, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Equal("kitchen", block!.Domain);
        Assert.DoesNotContain(block.Warnings, w => w.Contains("is a project", StringComparison.Ordinal));
    }

    [Fact]
    public void Assemble_without_a_domain_gives_a_cross_domain_overview()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("commons", "memory_rule", "Baseline", null, """{ "description": "always", "always_apply": true }""", null, "rule-base", "me");
        repo.Upsert("development", "fact", "Dev note", "alpha in dev", """{ "statement": "x" }""", null, "dev-alpha", "me");
        repo.Upsert("kitchen", "fact", "Kitchen note", "alpha in kitchen", """{ "statement": "x" }""", null, "kit-alpha", "me");

        var block = new ContextAssembler(repo, skills).Assemble("alpha", domain: null, 10, includeLinks: false, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Equal("*", block!.Domain);
        Assert.Contains(block.Warnings, w => w.Contains("cross-domain overview", StringComparison.Ordinal));
        Assert.Contains(block.Recall.Hits, h => h.Domain == "development"); // notes from multiple domains
        Assert.Contains(block.Recall.Hits, h => h.Domain == "kitchen");
        Assert.Contains(block.Rules, r => r.Title == "Baseline");            // commons rules still in force
    }

    [Fact]
    public void Assemble_without_a_domain_never_leaves_the_scope_boundary()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("development", "fact", "Dev note", "alpha in dev", """{ "statement": "x" }""", null, "dev-alpha", "me");
        repo.Upsert("kitchen", "fact", "Kitchen note", "alpha in kitchen", """{ "statement": "x" }""", null, "kit-alpha", "me");

        var block = new ContextAssembler(repo, skills)
            .Assemble("alpha", domain: null, 10, includeLinks: false, RequestScope.ForDomains(new[] { "kitchen" }));

        Assert.NotNull(block);
        Assert.Equal("*", block!.Domain);
        Assert.Contains(block.Recall.Hits, h => h.Domain == "kitchen");
        Assert.DoesNotContain(block.Recall.Hits, h => h.Domain == "development"); // out-of-scope domain never leaks
    }

    [Fact]
    public void Assemble_self_limits_recall_with_a_default_budget()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "fact", "Borscht", "beetroot soup", """{ "statement": "x" }""", null, "borscht", "me");

        var block = new ContextAssembler(repo, skills).Assemble("borscht", "kitchen", 10, includeLinks: false, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Equal(6000, block!.Recall.BudgetChars);                  // MEMP-214 default budget applied
        Assert.All(block.Recall.Hits, h => Assert.Null(h.PayloadJson)); // hits are lean
    }

    [Fact]
    public void Assemble_returns_lean_rules_without_verbose_arrays()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "memory_rule", "Salted", null,
            """{ "description": "salt early", "priority": 3, "trigger_phrases": ["a","b"], "source_refs": ["ref1"] }""", null, "rule-salt", "me");

        var block = new ContextAssembler(repo, skills).Assemble("x", "kitchen", 5, includeLinks: false, RequestScope.Unrestricted);

        var rule = block!.Rules.Single(r => r.Title == "Salted");
        Assert.Contains("salt early", rule.PayloadJson!, StringComparison.Ordinal);       // decision-relevant kept
        Assert.DoesNotContain("trigger_phrases", rule.PayloadJson!, StringComparison.Ordinal); // verbose array dropped
        Assert.DoesNotContain("source_refs", rule.PayloadJson!, StringComparison.Ordinal);
        Assert.Null(rule.TagsJson);
    }

    [Fact]
    public void Assemble_can_exclude_rules_and_skills()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "memory_rule", "R", null, """{ "description": "x", "always_apply": true }""", null, "rule-x", "me");
        skills.Upsert("kitchen", "recipe-authoring", "Recipe authoring", "write recipes", "recipe", 1, null, "me");
        repo.Upsert("kitchen", "fact", "Borscht", "beetroot soup", """{ "statement": "x" }""", null, "borscht", "me");

        var block = new ContextAssembler(repo, skills).Assemble("borscht", "kitchen", 10, includeLinks: false,
            RequestScope.Unrestricted, options: new ContextOptions(IncludeRules: false, IncludeSkills: false));

        Assert.NotNull(block);
        Assert.Empty(block!.Rules);                                    // rules section suppressed
        Assert.Empty(block.Skills);                                    // skills section suppressed
        Assert.Contains(block.Recall.Hits, h => h.Title == "Borscht"); // recall still works
    }

    [Fact]
    public void Assemble_can_filter_recall_by_type()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("development", "fact", "Alpha fact", "alpha gizmo", """{ "statement": "x" }""", null, "alpha-fact", "me");
        repo.Upsert("development", "backlog_item", "Alpha task", "alpha gizmo", """{ "key": "TST-100", "status": "ready" }""", null, "TST-100", "me");

        var block = new ContextAssembler(repo, skills).Assemble("alpha", "development", 10, includeLinks: false,
            RequestScope.Unrestricted, options: new ContextOptions(Types: new[] { "fact" }));

        Assert.NotNull(block);
        Assert.NotEmpty(block!.Recall.Hits);
        Assert.All(block.Recall.Hits, h => Assert.Equal("fact", h.Type));
    }

    [Fact]
    public void On_demand_domain_general_rule_is_gated_by_query_relevance()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        // Domain-general (project null), on-demand, with triggers — the npm-style situational rule.
        repo.Upsert("development", "memory_rule", "Npm gotcha", null,
            """{ "description": "npm registry override", "always_apply": false, "trigger_phrases": ["npm registry", "scaffold node project"] }""",
            null, "npm-rule", "me");
        repo.Upsert("development", "memory_rule", "Universal", null, """{ "description": "x", "always_apply": true }""", null, "uni", "me");

        var trading = new ContextAssembler(repo, skills).Assemble("mining rush cards review", "development", 10, false, RequestScope.Unrestricted, project: "binance-maf-trader");
        Assert.DoesNotContain(trading!.Rules, r => r.Title == "Npm gotcha"); // no trigger match -> not surfaced in an unrelated project
        Assert.Contains(trading.Rules, r => r.Title == "Universal");         // always_apply stays baseline

        var node = new ContextAssembler(repo, skills).Assemble("scaffold node project with npm", "development", 10, false, RequestScope.Unrestricted, project: "binance-maf-trader");
        Assert.Contains(node!.Rules, r => r.Title == "Npm gotcha");          // trigger match -> surfaced on demand
    }

    [Fact]
    public void Project_scoped_rule_is_not_gated_in_its_own_project()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("development", "memory_rule", "Unity arch", null,
            """{ "description": "addressables loading", "always_apply": false, "trigger_phrases": ["addressables"] }""",
            null, "unity-arch", "me", project: "unity-solitaire");

        var block = new ContextAssembler(repo, skills).Assemble("something totally unrelated", "development", 10, false, RequestScope.Unrestricted, project: "unity-solitaire");

        Assert.Contains(block!.Rules, r => r.Title == "Unity arch"); // project-scoped rules always load in their project
    }

    private static (NotesRepository Repo, SkillsService Skills) New(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        return (repo, new SkillsService(repo));
    }

    /// <summary>
    /// MEMP-257: a plain recall does NOT drag the catalogue along. Looking something up is not a request to be
    /// instructed, and every skill returned with a null body was the cost the caller could not opt out of.
    /// </summary>
    [Fact]
    public void A_plain_recall_query_is_offered_no_skills()
    {
        using var temp = new TempDatabase();
        var (repo, skills) = New(temp);
        repo.Upsert("kitchen", "fact", "Borscht", "beetroot soup", """{ "statement": "borscht is beetroot soup" }""", null, "borscht", "me");
        skills.Upsert("kitchen", "recipe-authoring", "Recipe authoring",
            "Writes recipes consistently. Use when authoring or editing a recipe.", "recipe", 1, null, "me");

        var block = new ContextAssembler(repo, skills).Assemble(
            "borscht", "kitchen", 10, includeLinks: false, RequestScope.Unrestricted);

        Assert.NotNull(block);
        Assert.Empty(block!.Skills);
        Assert.Empty(block.ActivatedSkills ?? []);
        // The recall the caller actually asked for is untouched.
        Assert.Contains(block.Recall.Hits, hit => hit.Title == "Borscht");
    }
}
