using MemoryMcp.Core.Schemas;
using MemoryMcp.Tests.Storage;
using MemoryMcp.Core.Storage;
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

    /// <summary>
    /// The payoff of composition (MEMP-268): <c>pinned</c> and <c>importance</c> are signals the ranker already
    /// reads, but the closed built-in types declared neither — so the payload form of a UNIVERSAL signal was
    /// unreachable for most types, and only the <c>pinned</c> TAG worked. One trait fixes every type at once,
    /// which is the clearest argument for having composition at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidPayloads))]
    public void Every_type_accepts_the_universal_ranking_signals_it_composes(string type, string payload)
    {
        var withSignals = payload.TrimEnd().TrimEnd('}') + """, "pinned": true, "importance": 7 }""";

        var result = Validator.Validate(type, withSignals);

        Assert.True(result.IsValid,
            $"{type} rejected the rankable trait's own fields: {string.Join("; ", result.Errors)}");
    }

    /// <summary>
    /// The trait constrains as well as permits: an importance outside 0-10 is a mistake, and a closed leaf must
    /// not accept it just because the trait contributed the field.
    /// </summary>
    [Fact]
    public void A_composed_field_is_still_validated_against_the_traits_own_rules()
    {
        Assert.False(Validator.Validate("fact", """{ "statement": "x", "importance": 99 }""").IsValid);
        Assert.False(Validator.Validate("fact", """{ "statement": "x", "pinned": "yes" }""").IsValid);
    }

    /// <summary>
    /// A dangling <c>$ref</c> is refused when the schema is AUTHORED, not later when a note happens to be
    /// validated against it. Deferred, the failure surfaces as a rejected write on an unrelated note, long
    /// after whoever could fix the schema has moved on.
    /// </summary>
    [Fact]
    public void A_reference_that_resolves_to_nothing_is_refused_at_authoring_time()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();

        var error = Assert.Throws<SchemaAuthoringException>(() => registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "allOf": [{ "$ref": "memory:trait/no-such-trait@1" }],
              "properties": { "note": { "type": "string" } }
            }
            """, "test"));

        Assert.Contains("cannot be resolved", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a schema composing a REAL trait is accepted, so the check does not simply refuse composition.</summary>
    [Fact]
    public void A_schema_composing_a_registered_trait_is_accepted()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();

        registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "allOf": [{ "$ref": "memory:trait/rankable@1" }],
              "unevaluatedProperties": false,
              "properties": { "note": { "type": "string" } }
            }
            """, "test");

        var validator = new SchemaValidator(registry);
        Assert.True(validator.Validate("widget", """{ "note": "x", "pinned": true }""").IsValid);
        Assert.False(validator.Validate("widget", """{ "note": "x", "nonsense": 1 }""").IsValid);
    }
}
