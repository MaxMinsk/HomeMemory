using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-262: editing a type's retrieval annotations is the whole procedure — the stored search text catches up
/// by itself, with no command for anyone to remember.
/// </summary>
public class LaneRebuildTests
{
    private const string Plain = """
        {
          "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
          "properties": { "note": { "type": "string" }, "code": { "type": "string" } }
        }
        """;

    // Same contract, different lexical intent: the note text becomes primary and the code becomes noise.
    private const string Annotated = """
        {
          "$id": "widget@1", "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
          "x-retrieval": { "version": "r1", "class": "canonical" },
          "properties": {
            "note": { "type": "string", "x-retrieval": { "lexical": "primary" } },
            "code": { "type": "string", "x-retrieval": { "lexical": "none" } }
          }
        }
        """;

    [Fact]
    public void An_edited_mapping_makes_its_type_stale_and_rebuilding_brings_it_back()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        registry.Upsert(factory, Plain, "test");

        var projector = new SchemaRetrievalProjector(registry);
        var notes = new NotesRepository(factory, registry);
        notes.Upsert("kitchen", "widget", "A widget", "the body",
            """{ "note": "findable prose", "code": "XJ-9" }""", null, "w1", "tester");

        var rebuilder = new LaneRebuilder(factory, projector);

        // Never laned under a recorded mapping, so the type starts stale.
        Assert.Contains("widget", rebuilder.TypesNeedingRebuild());
        rebuilder.Rebuild("widget", "2026-08-18T00:00:00Z");
        Assert.DoesNotContain("widget", rebuilder.TypesNeedingRebuild());
        Assert.Contains("XJ-9", Lanes(factory), StringComparison.Ordinal);

        // Editing ONLY the annotations keeps the version, so nothing but the mapping hash moves.
        registry.Upsert(factory, Annotated, "test");

        Assert.Contains("widget", rebuilder.TypesNeedingRebuild());
        Assert.Equal(1, rebuilder.Rebuild("widget", "2026-08-18T01:00:00Z"));
        Assert.DoesNotContain("widget", rebuilder.TypesNeedingRebuild());

        var after = Lanes(factory);
        Assert.Contains("findable prose", after, StringComparison.Ordinal);
        // Declared noise: gone from the index entirely, which is the point of declaring it.
        Assert.DoesNotContain("XJ-9", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rebuild that changes nothing must not rewrite rows: every start would otherwise touch every note and
    /// fire an index refresh for each of them.
    /// </summary>
    [Fact]
    public void Rebuilding_an_unchanged_type_updates_no_rows()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var notes = new NotesRepository(factory, registry);
        notes.Upsert("kitchen", "fact", "A fact", "body", """{ "statement": "x" }""", null, "f1", "tester");

        var rebuilder = new LaneRebuilder(factory, new SchemaRetrievalProjector(registry));
        rebuilder.Rebuild("fact", "2026-08-18T00:00:00Z");

        Assert.Equal(0, rebuilder.Rebuild("fact", "2026-08-18T01:00:00Z"));
    }

    private static string Lanes(ISqliteConnectionFactory factory)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(lane_primary, '') || ' ' || COALESCE(lane_secondary, '') FROM notes;";
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }
}
