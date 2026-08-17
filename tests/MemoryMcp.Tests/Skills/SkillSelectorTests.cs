using MemoryMcp.Core.Skills;
using Xunit;

namespace MemoryMcp.Tests.Skills;

/// <summary>
/// MEMP-257: the catalogue is narrowed to what the task needs, instead of every skill being listed with a null
/// body.
/// <para>The scenarios are the ones the ticket named, and the last of them is the observed failure that
/// prompted it: an architecture task was offered <c>frontend-design</c>.</para>
/// </summary>
public class SkillSelectorTests
{
    private static Skill Make(string key, string title, string summary, string? tags = null,
        string? project = null, string body = "instructions") =>
        new(key, title, null, 1, summary, body, project, null, tags, "commons");

    private static readonly Skill[] Catalogue =
    [
        Make("sprint-release", "Sprint release procedure",
            "Cuts a release. Use when asked to ship or release a sprint or a version.",
            """["intent:release","area:ops"]""", "memory-mcp"),
        Make("backlog-management", "Backlog management",
            "Runs the sprint backlog. Use when asked what is in the sprint, or to add or close a ticket.",
            """["intent:backlog","area:ops"]""", "memory-mcp"),
        Make("memory-authoring", "Memory authoring core conventions",
            "Defines how to author notes. Use when about to write or edit any note.",
            """["intent:memory-write","area:schemas"]"""),
        Make("memory-search-syntax", "Memory search and recall query syntax",
            "Explains how a query is interpreted. Use when a search returned the wrong note.",
            """["intent:memory-read","area:search"]"""),
        Make("frontend-design", "Frontend Design, distinctive visual design",
            "Directs UI work toward a distinctive visual identity. Use when building a new page or interface.",
            """["area:frontend"]"""),
        Make("source-ingest", "Source ingest",
            "Captures an external source into memory. Use when the user shares a link worth remembering.",
            """["intent:ingest"]"""),
    ];

    private static (IReadOnlyList<Skill> Candidates, IReadOnlyList<SelectedSkill> Activated) Run(
        string query, string? project = "memory-mcp") =>
        SkillSelector.Select(Catalogue, query, project, skill => skill);

    [Theory]
    [InlineData("release a sprint and publish the add-on", "sprint-release")]
    [InlineData("what is in the sprint backlog, close a ticket", "backlog-management")]
    [InlineData("how do I write a note in memory", "memory-authoring")]
    [InlineData("build a new page for the viewer interface", "frontend-design")]
    public void The_skill_a_task_needs_is_offered_first(string query, string expected)
    {
        var (candidates, _) = Run(query);

        Assert.NotEmpty(candidates);
        Assert.Equal(expected, candidates[0].Key);
    }

    /// <summary>
    /// The observed failure this ticket exists for: an architecture task was offered <c>frontend-design</c>
    /// alongside seven other skills, none of them chosen for anything.
    /// </summary>
    [Fact]
    public void An_architecture_task_is_not_offered_the_frontend_skill()
    {
        var (candidates, _) = Run("schema driven retrieval architecture, traits and composition");

        Assert.DoesNotContain(candidates, skill => skill.Key == "frontend-design");
    }

    /// <summary>A household task matches nothing, and nothing is what it should get.</summary>
    [Fact]
    public void An_unrelated_task_activates_nothing()
    {
        var (candidates, activated) = Run("what should I cook for dinner tonight");

        Assert.Empty(activated);
        Assert.DoesNotContain(candidates, skill => skill.Key == "frontend-design");
    }

    /// <summary>Candidates carry no body — a descriptor is for choosing, not for following.</summary>
    [Fact]
    public void Candidates_are_descriptors_and_activated_skills_carry_the_instructions()
    {
        var (candidates, activated) = Run("release a sprint");

        Assert.All(candidates, skill => Assert.Null(skill.Body));
        Assert.NotEmpty(activated);
        Assert.All(activated, selected => Assert.False(string.IsNullOrEmpty(selected.Skill.Body)));
        Assert.All(activated, selected => Assert.False(string.IsNullOrWhiteSpace(selected.Reason)));
    }

    [Fact]
    public void No_more_than_the_caps_are_returned()
    {
        var (candidates, activated) = Run("memory release backlog note search ingest interface sprint");

        Assert.True(candidates.Count <= SkillSelector.MaxCandidates, $"got {candidates.Count} candidates");
        Assert.True(activated.Count <= SkillSelector.MaxActivated, $"got {activated.Count} activated");
    }

    /// <summary>
    /// A skill belonging to ANOTHER project is almost never what this task wants, however well its words match —
    /// two projects sharing a domain both have a "sprint release" skill, and only one of them is right.
    /// </summary>
    [Fact]
    public void Another_projects_skill_loses_to_the_task_s_own()
    {
        var mine = Make("sprint-release", "Sprint release procedure", "Cuts a release.",
            """["intent:release"]""", "memory-mcp");
        var theirs = Make("sprint-release", "Sprint release procedure", "Cuts a release.",
            """["intent:release"]""", "some-other-project");

        var (candidates, _) = SkillSelector.Select([theirs, mine], "release a sprint", "memory-mcp", skill => skill);

        Assert.Equal("memory-mcp", candidates[0].Project);
    }

    /// <summary>With no query there is nothing to be relevant to; returning the catalogue anyway is the bug.</summary>
    [Fact]
    public void A_blank_query_selects_nothing_rather_than_everything()
    {
        var (candidates, activated) = SkillSelector.Select(Catalogue, null, null, skill => skill);

        Assert.Empty(candidates);
        Assert.Empty(activated);
    }

    /// <summary>The instruction budget is a hard cap: a huge body is skipped rather than allowed to swamp the block.</summary>
    [Fact]
    public void An_oversized_instruction_body_is_not_delivered()
    {
        var huge = Make("sprint-release", "Sprint release procedure", "Cuts a release. Use when releasing.",
            """["intent:release"]""", "memory-mcp", new string('x', SkillSelector.ActivationBudgetChars + 1));

        var (candidates, activated) = SkillSelector.Select([huge], "release a sprint", "memory-mcp", skill => skill);

        Assert.NotEmpty(candidates);
        Assert.Empty(activated);
    }
}
