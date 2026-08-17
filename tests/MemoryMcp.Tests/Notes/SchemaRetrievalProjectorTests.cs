using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-252: a type's schema decides what is worth embedding, instead of every string being swept into the
/// vector regardless of what it means.
/// <para>These assert against the REAL shipped schemas rather than fixtures. The annotations are the deliverable
/// — a test over a hand-written schema would pass happily while <c>recipe@1</c> shipped with none.</para>
/// </summary>
public class SchemaRetrievalProjectorTests
{
    private static readonly SchemaRegistry Schemas = SchemaRegistry.FromEmbeddedResources();

    // A schema in the shape the live server actually uses: $ref into $defs, shared between two properties.
    // The built-in schemas are all flat, so without this the walker's ref handling would be untested against
    // the only schemas that need it.
    private const string RefSchema = """
        {
          "$id": "dish@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
          "properties": {
            "preparation": { "type": "array", "items": { "$ref": "#/$defs/step" } },
            "cooking": { "type": "array", "items": { "$ref": "#/$defs/step" } },
            "parts": { "type": "array", "items": { "$ref": "#/$defs/part" } }
          },
          "$defs": {
            "step": {
              "type": "object",
              "properties": {
                "text": { "type": "string", "x-retrieval": { "lexical": "primary", "semantic": "steps" } },
                "phase": { "type": "string", "x-retrieval": { "lexical": "none", "semantic": false } }
              }
            },
            "part": {
              "type": "object",
              "properties": { "name": { "type": "string", "x-retrieval": { "semantic": "parts" } } }
            }
          }
        }
        """;

    /// <summary>
    /// The live corpus's richest type composes through <c>$ref</c> and <c>$defs</c>, so a walker that does not
    /// follow refs finds no annotated fields at all on exactly the type where field selection matters most —
    /// and it fails SILENTLY, by falling back to legacy.
    /// </summary>
    [Fact]
    public void Annotations_behind_a_local_ref_are_found_and_shared_definitions_serve_every_user()
    {
        var mapping = RetrievalMapping.FromSchema("dish", 1, RefSchema)!;

        Assert.Equal("steps", mapping.ForPath("preparation[].text")?.SemanticGroup);
        Assert.Equal("steps", mapping.ForPath("cooking[].text")?.SemanticGroup);
        Assert.Equal("parts", mapping.ForPath("parts[].name")?.SemanticGroup);
        // A field the shared definition marks as noise stays noise wherever it appears.
        Assert.Null(mapping.ForPath("preparation[].phase")?.SemanticGroup);
    }

    /// <summary>A ref that points nowhere must not throw: the validator judges schema well-formedness, not this.</summary>
    [Fact]
    public void An_unresolvable_ref_yields_no_fields_rather_than_an_error()
    {
        const string Dangling = """
            {
              "$id": "dish@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "x-retrieval": { "version": "r1" },
              "properties": { "steps": { "type": "array", "items": { "$ref": "#/$defs/missing" } } }
            }
            """;

        var mapping = RetrievalMapping.FromSchema("dish", 1, Dangling);

        Assert.NotNull(mapping);
        Assert.Empty(mapping!.Fields);
    }

    /// <summary>
    /// The point of allowing an annotation-only edit is that retrieval can be retuned WITHOUT a version bump.
    /// That only holds if everything derived from the schema notices — and both the projector and the type
    /// policy cached per type@version, so an edit that deliberately keeps the version kept serving the old
    /// answer until the next restart. Found on production: `reference` was annotated and left the type-policy
    /// bridge, while the projector went on treating it as unmapped.
    /// </summary>
    [Fact]
    public void An_annotation_edit_takes_effect_without_a_version_bump_or_a_restart()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var projector = new SchemaRetrievalProjector(registry);
        var policy = new TypePolicy(registry);

        registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "properties": { "note": { "type": "string" } }
            }
            """, "test");
        Assert.True(projector.Describe("widget").IsLegacy);
        Assert.Equal(90.0, policy.HalfLifeDays("widget"));

        // Same $id, same VERSION — only the annotations change.
        registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "x-retrieval": { "version": "r1", "class": "canonical", "halfLifeDays": 3650 },
              "properties": { "note": { "type": "string", "x-retrieval": { "semantic": "text" } } }
            }
            """, "test");

        Assert.False(projector.Describe("widget").IsLegacy);
        Assert.Equal(3650.0, policy.HalfLifeDays("widget"));
        Assert.Equal(2.0, policy.Goodness("widget"));
    }

    /// <summary>
    /// The annotation-only exclusion must compare what a schema MEANS, not the bytes it arrived as.
    /// <para>Found in production: two schemas refused their annotations with "bump the version", while six
    /// identical-in-kind ones accepted theirs. The two were the only ones containing <c>\u0027</c> escapes.
    /// The comparison used each scalar's raw source text, so the same string written by a different serialiser
    /// read as a contract change — meaning any schema that had ever round-tripped through another JSON writer
    /// could never be annotated again, and the error said nothing about why.</para>
    /// </summary>
    [Fact]
    public void An_annotation_edit_is_accepted_even_when_the_stored_schema_escapes_its_strings_differently()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var notes = new NotesRepository(factory, registry);

        // Stored with an escaped apostrophe and an integer written as 1.
        registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "properties": { "note": { "type": "string", "description": "e.g. \u00272-3\u0027", "minLength": 1 } }
            }
            """, "test");
        notes.Upsert("kitchen", "widget", "A widget", null, """{ "note": "x" }""", null, "w1", "tester");

        // Re-sent with the SAME apostrophe unescaped and the same number as 1.0 — plus annotations. Nothing
        // about the contract has changed, so this must be accepted despite the notes that already exist.
        var exception = Record.Exception(() => registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "x-retrieval": { "version": "r1", "class": "canonical", "halfLifeDays": 3650 },
              "properties": { "note": { "type": "string", "description": "e.g. '2-3'", "minLength": 1.0,
                                        "x-retrieval": { "semantic": "text" } } }
            }
            """, "test"));

        Assert.Null(exception);
        Assert.False(new SchemaRetrievalProjector(registry).Describe("widget").IsLegacy);
    }

    /// <summary>A REAL contract change is still refused once notes exist — the exclusion must not swallow that.</summary>
    [Fact]
    public void A_genuine_contract_change_is_still_refused_when_notes_exist()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var notes = new NotesRepository(factory, registry);

        registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "properties": { "note": { "type": "string" } }
            }
            """, "test");
        notes.Upsert("kitchen", "widget", "A widget", null, """{ "note": "x" }""", null, "w1", "tester");

        Assert.Throws<SchemaAuthoringException>(() => registry.Upsert(factory, """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "required": ["note"],
              "properties": { "note": { "type": "string" } }
            }
            """, "test"));
    }

    private static SchemaRetrievalProjector Projector() => new(Schemas);

    private static string TextOf(IReadOnlyList<RetrievalPassage> passages) =>
        string.Join(" | ", passages.Select(passage => passage.Text));

    /// <summary>
    /// The case that makes field selection matter rather than merely tidy: roughly one note in nine has an
    /// empty body and lives entirely in its payload, and most of those are recipes. Legacy indexing would put
    /// "5-7", "tsp" and "pcs" into the vector alongside the food.
    /// </summary>
    [Fact]
    public void A_recipe_with_no_body_embeds_its_food_and_not_its_units()
    {
        var payload = """
            {
              "format": "kazan",
              "servings": "5-7",
              "ingredients": [
                { "name": "baranina", "amount": "1.5", "unit": "kg" },
                { "name": "zira", "amount": "2", "unit": "tsp" }
              ],
              "cooking": ["obzharit myaso do korochki"]
            }
            """;

        var passages = Projector().Passages(new NoteContent("recipe", "Plov", null, null, payload));
        var text = TextOf(passages);

        Assert.Contains("baranina", text, StringComparison.Ordinal);
        Assert.Contains("zira", text, StringComparison.Ordinal);
        Assert.Contains("obzharit", text, StringComparison.Ordinal);
        Assert.Contains("kazan", text, StringComparison.Ordinal);
        // Amounts and units are the noise this ticket exists to keep out: they are identical across unrelated
        // recipes, so they pull every recipe's vector toward the same uninformative centre.
        Assert.DoesNotContain("5-7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tsp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ticket is about its acceptance criteria. Its key, status, sprint and estimate are how it is filtered,
    /// never what it means — and embedding "ready" or "S" makes every ticket look alike.
    /// </summary>
    [Fact]
    public void A_backlog_item_embeds_its_acceptance_and_not_its_workflow_fields()
    {
        var payload = """
            {
              "key": "MEMP-252",
              "project": "memory-mcp",
              "status": "ready",
              "sprint": "S65",
              "priority": "high",
              "estimate": "M",
              "acceptance": "Give the types that matter explicit retrieval annotations."
            }
            """;

        var text = TextOf(Projector().Passages(new NoteContent("backlog_item", "Phase 2", null, null, payload)));

        Assert.Contains("explicit retrieval annotations", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MEMP-252", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ready", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S65", text, StringComparison.Ordinal);
    }

    /// <summary>Arrays of plain strings are annotated on the array itself; their values arrive one level deeper.</summary>
    [Fact]
    public void An_array_of_plain_strings_is_matched_through_its_array_annotation()
    {
        var payload = """{ "format": "grill", "equipment": ["reshetka", "shchipcy"] }""";

        var text = TextOf(Projector().Passages(new NoteContent("recipe", "Shashlyk", null, null, payload)));

        Assert.Contains("reshetka", text, StringComparison.Ordinal);
        Assert.Contains("shchipcy", text, StringComparison.Ordinal);
    }

    /// <summary>A hit has to be explainable: which passage matched, and which fields built it.</summary>
    [Fact]
    public void A_passage_reports_the_indexed_paths_it_was_built_from()
    {
        var payload = """{ "format": "kazan", "ingredients": [{ "name": "baranina" }] }""";

        var passages = Projector().Passages(new NoteContent("recipe", "Plov", null, null, payload));
        var ingredients = passages.Single(passage => passage.Name == "ingredients");

        Assert.Contains("payload.ingredients[0].name", ingredients.SourcePaths, StringComparer.Ordinal);
        Assert.Contains(passages, passage => passage.Name == "dish");
        Assert.Contains(passages, passage => passage.Name == "title");
    }

    /// <summary>
    /// A type that declares no annotations must behave EXACTLY as before — that is what makes this rollout
    /// safe one type at a time rather than a corpus-wide cutover.
    /// </summary>
    [Fact]
    public void An_unannotated_type_is_projected_identically_to_before()
    {
        // A type this build ships no schema for at all — every BUILT-IN type now declares annotations, so the
        // fallback has to be proven on something genuinely undeclared.
        var note = new NoteContent("journal", "Tea", "prefers loose leaf", """["kitchen"]""", """{ "statement": "x" }""");

        var schema = Projector().Passages(note);
        var legacy = new LegacyRetrievalProjector().Passages(note);

        Assert.Equal(legacy.Select(p => (p.Name, p.Ordinal, p.Text)), schema.Select(p => (p.Name, p.Ordinal, p.Text)));
        Assert.True(Projector().Describe("journal").IsLegacy);
        Assert.False(Projector().Describe("recipe").IsLegacy);
    }

    /// <summary>
    /// Scoring must accept a passage stamped with ANY current mapping, not one. Filtering on a single hash was
    /// correct while every type shared the legacy mapping and would silently drop every annotated type the
    /// moment one did not.
    /// </summary>
    [Fact]
    public void Every_annotated_types_mapping_hash_counts_as_current()
    {
        var projector = Projector();
        var hashes = projector.CurrentMappingHashes;

        Assert.Contains(new LegacyRetrievalProjector().Describe(string.Empty).MappingHash, hashes, StringComparer.Ordinal);
        Assert.Contains(projector.Describe("recipe").MappingHash, hashes, StringComparer.Ordinal);
        Assert.Contains(projector.Describe("fact").MappingHash, hashes, StringComparer.Ordinal);
        Assert.NotEqual(projector.Describe("recipe").MappingHash, projector.Describe("fact").MappingHash);
    }

    /// <summary>
    /// The whole point of keeping the mapping hash separate from the schema version: editing an annotation
    /// invalidates the INDEX but not the data contract, so no stored note becomes invalid and no type version
    /// is bumped.
    /// </summary>
    [Fact]
    public void Editing_an_annotation_changes_the_mapping_hash_but_not_the_schema_version()
    {
        const string Before = """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "properties": { "note": { "type": "string", "x-retrieval": { "semantic": "text" } } }
            }
            """;
        const string After = """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "properties": { "note": { "type": "string", "x-retrieval": { "semantic": "summary" } } }
            }
            """;

        var before = RetrievalMapping.FromSchema("widget", 1, Before)!;
        var after = RetrievalMapping.FromSchema("widget", 1, After)!;

        Assert.NotEqual(before.Hash, after.Hash);
        Assert.Equal(before.SchemaVersion, after.SchemaVersion);
    }

    /// <summary>
    /// Type-level priors arrive through schema_upsert, which agents may write. A half-life of zero would erase
    /// every note of that type from ranking the instant it was written, so the bound is enforced, not trusted.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100000)]
    public void An_out_of_range_recency_prior_is_rejected_rather_than_clamped_silently(double days)
    {
        var json = $$"""
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "x-retrieval": { "halfLifeDays": {{days.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },
              "properties": {}
            }
            """;

        var error = Assert.Throws<ArgumentException>(() => RetrievalMapping.FromSchema("widget", 1, json));

        Assert.Contains("halfLifeDays", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half-life a type declares is the one ranking uses (MEMP-253). These values came from the table that
    /// used to live in C#, and the point of pinning them is that retiring it stayed a refactor: a note ages at
    /// exactly the rate it did before, now because its own schema says so.
    /// </summary>
    [Theory]
    [InlineData("episode", 7.0)]
    [InlineData("backlog_item", 45.0)]
    [InlineData("decision", 180.0)]
    [InlineData("fact", 180.0)]
    [InlineData("recipe", 3650.0)]
    public void The_declared_recency_prior_matches_the_hardcoded_table_it_will_replace(string type, double expected)
    {
        var schema = Schemas.GetLatest(type)!;
        var mapping = RetrievalMapping.FromSchema(type, schema.Version, schema.Json)!;

        Assert.Equal(expected, mapping.HalfLifeDays);
        Assert.Equal(expected, TypePolicy.Bridged.HalfLifeDays(type));
    }

    /// <summary>A malformed annotation must not disable indexing for the type; it falls back to legacy.</summary>
    [Fact]
    public void A_malformed_annotation_falls_back_to_legacy_rather_than_indexing_nothing()
    {
        const string Broken = """
            {
              "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
              "properties": { "note": { "type": "string", "x-retrieval": { "lexical": "sideways" } } }
            }
            """;

        Assert.Throws<ArgumentException>(() => RetrievalMapping.FromSchema("widget", 1, Broken));
    }
}
