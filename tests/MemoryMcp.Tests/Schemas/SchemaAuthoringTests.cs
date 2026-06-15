using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Schemas;

public class SchemaAuthoringTests
{
    private const string SpiceSchema = """
        { "$id": "spice@1", "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object", "required": ["name"], "additionalProperties": false,
          "properties": { "name": { "type": "string" }, "heat": { "enum": ["mild", "medium", "hot"] } } }
        """;

    [Fact]
    public void Upsert_registers_agent_schema_and_it_validates_notes()
    {
        using var temp = new TempDatabase();
        var (registry, factory) = New(temp);

        var stored = registry.Upsert(factory, SpiceSchema);

        Assert.Equal("spice", stored.Type);
        Assert.False(registry.IsBuiltin("spice"));
        Assert.NotNull(registry.GetLatest("spice"));

        var validator = new SchemaValidator(registry);
        Assert.True(validator.Validate("spice", """{ "name": "cumin", "heat": "medium" }""").IsValid);
        Assert.False(validator.Validate("spice", """{ "heat": "blazing" }""").IsValid); // missing name + bad enum
    }

    [Fact]
    public void Upsert_rejects_builtin_type()
    {
        using var temp = new TempDatabase();
        var (registry, factory) = New(temp);

        var ex = Assert.Throws<SchemaAuthoringException>(() =>
            registry.Upsert(factory, """{ "$id": "backlog_item@1", "type": "object" }"""));
        Assert.Contains("built-in", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "type": "object" }""")]                  // missing $id
    [InlineData("""{ "$id": "spice", "type": "object" }""")]  // $id not type@version
    public void Upsert_rejects_bad_documents(string json)
    {
        using var temp = new TempDatabase();
        var (registry, factory) = New(temp);
        Assert.Throws<SchemaAuthoringException>(() => registry.Upsert(factory, json));
    }

    [Fact]
    public void Upsert_blocks_changing_a_version_in_use_but_allows_new_version()
    {
        using var temp = new TempDatabase();
        var (registry, factory) = New(temp);
        registry.Upsert(factory, SpiceSchema);
        new NotesRepository(factory, registry)
            .Upsert("kitchen", "spice", "Cumin", null, """{ "name": "cumin" }""", null, "cumin", "me");

        var changedV1 = """{ "$id": "spice@1", "type": "object", "required": ["name", "heat"], "additionalProperties": false, "properties": { "name": { "type": "string" }, "heat": { "type": "string" } } }""";
        Assert.Throws<SchemaAuthoringException>(() => registry.Upsert(factory, changedV1)); // in use

        registry.Upsert(factory, changedV1.Replace("spice@1", "spice@2", StringComparison.Ordinal)); // new version ok
        Assert.Equal(2, registry.GetLatest("spice")!.Version);
    }

    [Fact]
    public void Upsert_is_idempotent_for_identical_document_even_when_in_use()
    {
        using var temp = new TempDatabase();
        var (registry, factory) = New(temp);
        registry.Upsert(factory, SpiceSchema);
        new NotesRepository(factory, registry)
            .Upsert("kitchen", "spice", "Cumin", null, """{ "name": "cumin" }""", null, "cumin", "me");

        registry.Upsert(factory, SpiceSchema); // same content -> no-op, must not throw
        Assert.Equal(1, registry.GetLatest("spice")!.Version);
    }

    [Fact]
    public void LoadFromDatabase_restores_agent_schemas_into_a_fresh_registry()
    {
        using var temp = new TempDatabase();
        var (registry, factory) = New(temp);
        registry.Upsert(factory, SpiceSchema);

        var reloaded = SchemaRegistry.FromEmbeddedResources();
        reloaded.LoadFromDatabase(factory);

        Assert.NotNull(reloaded.GetLatest("spice"));
        Assert.True(new SchemaValidator(reloaded).Validate("spice", """{ "name": "cumin" }""").IsValid);
    }

    private static (SchemaRegistry Registry, SqliteConnectionFactory Factory) New(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        registry.SyncToDatabase(factory);
        return (registry, factory);
    }
}
