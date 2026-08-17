using MemoryMcp.Core.Schemas;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-253: how a type ranks, ages and lints is read from the type's own schema instead of from tables in C#.
/// <para>This was a REFACTOR, so most of these assert that nothing moved. The values are the ones the retired
/// tables held; they are pinned here because "the type decides" is only an improvement if the type decides the
/// same thing the table did.</para>
/// </summary>
public class TypePolicyTests
{
    /// <summary>The ranking class the retired <c>CanonicalTypes</c>/<c>EphemeralTypes</c> sets encoded.</summary>
    [Theory]
    [InlineData("memory_rule", 2.0)]
    [InlineData("skill", 2.0)]
    [InlineData("recipe", 2.0)]
    [InlineData("decision", 2.0)]
    [InlineData("project_state", 2.0)]
    [InlineData("preference", 2.0)]
    [InlineData("saved_search", 2.0)]
    [InlineData("episode", 0.0)]
    [InlineData("fact", 1.0)]
    [InlineData("backlog_item", 1.0)]
    [InlineData("sprint", 1.0)]
    public void Ranking_class_matches_the_sets_it_replaced(string type, double expected) =>
        Assert.Equal(expected, TypePolicy.Bridged.Goodness(type));

    /// <summary>The exemptions the retired <c>NoTagsExemptTypes</c> and <c>OrphanExemptTypes</c> literals encoded.</summary>
    [Theory]
    [InlineData("skill", false, false)]
    [InlineData("sprint", false, false)]
    [InlineData("saved_search", false, false)]
    [InlineData("memory_evolution_suggestion", false, false)]
    [InlineData("episode", true, false)]
    [InlineData("memory_rule", true, false)]
    [InlineData("preference", true, false)]
    [InlineData("fact", true, true)]
    [InlineData("recipe", true, true)]
    [InlineData("decision", true, true)]
    public void Lint_exemptions_match_the_literals_they_replaced(string type, bool expectsTags, bool expectsLinks)
    {
        var traits = TypePolicy.Bridged.For(type);

        Assert.Equal(expectsTags, traits.ExpectsTags);
        Assert.Equal(expectsLinks, traits.ExpectsLinks);
    }

    /// <summary>The fact horizon applied to exactly <c>fact</c> and <c>reference</c> before, and still does.</summary>
    [Fact]
    public void Only_claim_bearing_types_are_subject_to_the_fact_horizon()
    {
        Assert.True(TypePolicy.Bridged.IsClaimLike("fact"));
        Assert.True(TypePolicy.Bridged.IsClaimLike("reference"));
        Assert.False(TypePolicy.Bridged.IsClaimLike("recipe"));
        Assert.False(TypePolicy.Bridged.IsClaimLike("backlog_item"));
    }

    /// <summary>
    /// An unknown type takes the documented defaults rather than throwing or vanishing from the SQL — a new
    /// agent-authored type has to keep working before anyone annotates it.
    /// </summary>
    [Fact]
    public void An_unknown_type_takes_the_documented_defaults()
    {
        var traits = TypePolicy.Bridged.For("some_type_nobody_declared");

        Assert.Equal(TypeTraits.Default, traits);
        Assert.Equal(90.0, traits.HalfLifeDays);
        Assert.Equal(1.0, TypePolicy.Bridged.Goodness("some_type_nobody_declared"));
    }

    /// <summary>
    /// The bridge exists only for types whose schema cannot yet carry annotations, and it must shrink to nothing
    /// as MEMP-266 lands. Anything the repository already ships MUST have moved off it — if a built-in type
    /// reappears here, the annotation that was supposed to replace its table entry did not take effect.
    /// </summary>
    [Fact]
    public void No_type_this_build_ships_still_depends_on_the_bridge()
    {
        var shipped = SchemaRegistry.FromEmbeddedResources().All.Select(schema => schema.Type).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(TypePolicy.Bridged.TypesOnBridge, type => shipped.Contains(type));
        // And the ones that DO remain are the agent-authored types, named so the debt is visible.
        Assert.Contains("reference", TypePolicy.Bridged.TypesOnBridge);
    }

    /// <summary>
    /// Type names now reach SQL from an agent-writable registry rather than from constants, so a name carrying a
    /// quote must be escaped rather than trusted.
    /// </summary>
    [Fact]
    public void A_type_name_containing_a_quote_is_escaped_into_the_sql()
    {
        var clause = TypePolicy.Bridged.RecencyOrderByClause();

        // Every literal in the generated clause is balanced: an unescaped quote would leave an odd count.
        Assert.Equal(0, clause.Count(character => character == '\'') % 2);
        Assert.Contains("julianday", clause, StringComparison.Ordinal);
    }
}
