using MemoryMcp.Core.Schemas;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-268: every built-in type stays CLOSED — an unknown payload field is refused — while the keyword that
/// closes it changes from <c>additionalProperties</c> to <c>unevaluatedProperties</c>.
/// <para><b>Why this test exists before that change.</b> The two keywords are equivalent for a standalone
/// schema and emphatically not for a composed one: <c>additionalProperties</c> cannot see properties
/// contributed by an <c>allOf</c> branch, so a schema using it rejects the very fields its own trait declares.
/// Swapping is therefore a prerequisite for composition — but it touches the validation contract of every type
/// in the corpus, so "equivalent for a standalone schema" is verified here rather than believed.</para>
/// </summary>
public class SchemaClosureTests
{
    private static readonly SchemaValidator Validator = new(SchemaRegistry.FromEmbeddedResources());

    /// <summary>A minimal payload that satisfies each built-in type's required fields.</summary>
    public static TheoryData<string, string> ValidPayloads() => new()
    {
        { "fact", """{ "statement": "the pipe is 32mm" }""" },
        { "decision", """{ "decision": "recipe@1 is the source of truth" }""" },
        { "episode", """{ "summary": "sent the letter" }""" },
        { "recipe", """{ "format": "kazan" }""" },
        { "backlog_item", """{ "key": "MEMP-268", "status": "ready" }""" },
        { "preference", """{ "preference": "loose leaf tea" }""" },
        { "project_state", """{ "project": "memory-mcp", "state": "sprint 67 in progress" }""" },
        { "memory_rule", """{ "description": "never store secrets in memory" }""" },
        { "skill", """{ "key": "memory-authoring", "version": 4 }""" },
        { "saved_search", """{ "name": "open tickets" }""" },
        { "sprint", """{ "key": "S67", "status": "active" }""" },
        { "memory_evolution_suggestion", """{ "target_id": "abc", "rationale": "missing tags", "status": "open" }""" },
    };

    /// <summary>The payload each type is meant to accept must keep validating.</summary>
    [Theory]
    [MemberData(nameof(ValidPayloads))]
    public void A_valid_payload_is_accepted(string type, string payload)
    {
        var result = Validator.Validate(type, payload);

        Assert.True(result.IsValid, $"{type} rejected its own valid payload: {string.Join("; ", result.Errors)}");
    }

    /// <summary>
    /// And the type must stay CLOSED. This is the property at risk in the swap: get it wrong and every type
    /// silently starts accepting typo'd field names, which is exactly the class of error a schema exists to catch.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidPayloads))]
    public void An_unknown_field_is_still_refused(string type, string payload)
    {
        var withUnknown = payload.TrimEnd().TrimEnd('}') + """, "totally_unknown_field": "x" }""";

        var result = Validator.Validate(type, withUnknown);

        Assert.False(result.IsValid, $"{type} accepted an unknown field — the type is no longer closed");
    }

    /// <summary>
    /// Nested objects and array items are closed too, and are the easiest to miss: <c>recipe@1</c> closes its
    /// ingredient items and its control_points block separately from the root.
    /// </summary>
    [Fact]
    public void A_nested_object_is_closed_as_well_as_the_root()
    {
        var badIngredient = Validator.Validate(
            "recipe", """{ "format": "kazan", "ingredients": [{ "name": "zira", "nonsense": 1 }] }""");
        var badControlPoint = Validator.Validate(
            "recipe", """{ "format": "kazan", "control_points": { "heat": "high", "nonsense": 1 } }""");

        Assert.False(badIngredient.IsValid);
        Assert.False(badControlPoint.IsValid);
    }
}
