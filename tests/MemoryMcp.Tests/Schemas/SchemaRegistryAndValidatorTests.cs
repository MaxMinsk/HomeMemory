using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Schemas;

public class SchemaRegistryAndValidatorTests
{
    [Fact]
    public void Registry_loads_backlog_item_schema()
    {
        var registry = SchemaRegistry.FromEmbeddedResources();
        var definition = registry.GetLatest("backlog_item");

        Assert.NotNull(definition);
        Assert.Equal("backlog_item", definition!.Type);
        Assert.Equal(1, definition.Version);
    }

    [Fact]
    public void SyncToDatabase_is_idempotent()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();

        registry.SyncToDatabase(factory);
        registry.SyncToDatabase(factory);

        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM schemas WHERE type = 'backlog_item' AND version = 1;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Valid_backlog_item_passes()
    {
        var validator = new SchemaValidator(SchemaRegistry.FromEmbeddedResources());

        var result = validator.Validate("backlog_item",
            """{ "key": "MEMP-007", "status": "next", "priority": "high", "assignee": "agent", "sprint": "S1" }""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("""{ "status": "next" }""")]                             // missing key
    [InlineData("""{ "key": "MEMP-007" }""")]                            // missing status
    [InlineData("""{ "key": "memp-7", "status": "next" }""")]            // bad key pattern
    [InlineData("""{ "key": "MEMP-007", "status": "bogus" }""")]         // bad status enum
    [InlineData("""{ "key": "MEMP-007", "status": "next", "x": 1 }""")]  // additionalProperties
    public void Invalid_backlog_item_is_rejected(string payload)
    {
        var validator = new SchemaValidator(SchemaRegistry.FromEmbeddedResources());

        var result = validator.Validate("backlog_item", payload);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Unknown_type_is_rejected()
    {
        var validator = new SchemaValidator(SchemaRegistry.FromEmbeddedResources());

        var result = validator.Validate("does_not_exist", "{}");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Registry_loads_sprint_schema()
    {
        var definition = SchemaRegistry.FromEmbeddedResources().GetLatest("sprint");

        Assert.NotNull(definition);
        Assert.Equal(1, definition!.Version);
    }

    [Fact]
    public void Valid_sprint_passes()
    {
        var validator = new SchemaValidator(SchemaRegistry.FromEmbeddedResources());

        var result = validator.Validate("sprint",
            """{ "key": "S1", "goal": "Close M0 + sprint infra", "status": "active", "version_target": "0.2.0" }""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Invalid_sprint_status_is_rejected()
    {
        var validator = new SchemaValidator(SchemaRegistry.FromEmbeddedResources());

        var result = validator.Validate("sprint", """{ "key": "S1", "status": "bogus" }""");

        Assert.False(result.IsValid);
    }
}
