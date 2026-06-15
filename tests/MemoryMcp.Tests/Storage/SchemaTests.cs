using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Storage;

public class SchemaTests
{
    [Fact]
    public void Init_migration_creates_tables_and_indexes()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);

        var migrator = new Migrator(factory, SchemaMigrations.All);
        var version = migrator.Migrate();

        Assert.Equal(migrator.LatestVersion, version);
        using var connection = factory.Create();
        foreach (var table in new[] { "notes", "note_links", "note_events", "schemas" })
        {
            Assert.True(ObjectExists(connection, "table", table), $"missing table {table}");
        }
        foreach (var index in new[] { "ix_notes_domain_type", "ux_notes_dedup" })
        {
            Assert.True(ObjectExists(connection, "index", index), $"missing index {index}");
        }
    }

    [Fact]
    public void Dedup_index_blocks_duplicate_key_within_domain_and_type()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        using var connection = factory.Create();

        InsertNote(connection, id: "1", dedupKey: "k");
        var ex = Assert.Throws<SqliteException>(() => InsertNote(connection, id: "2", dedupKey: "k"));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void Null_dedup_key_allows_multiple_rows()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        using var connection = factory.Create();

        InsertNote(connection, id: "1", dedupKey: null);
        InsertNote(connection, id: "2", dedupKey: null); // must not throw
    }

    [Fact]
    public void Note_has_expected_column_defaults()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        using var connection = factory.Create();

        InsertNote(connection, id: "1", dedupKey: null);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, deleted, schema_ver FROM notes WHERE id = '1';";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("active", reader.GetString(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    private static void InsertNote(SqliteConnection connection, string id, string? dedupKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO notes (id, domain, type, dedup_key, created_utc, updated_utc) " +
            "VALUES ($id, 'memory-mcp', 'backlog_item', $dedup, '2026-06-15T00:00:00Z', '2026-06-15T00:00:00Z');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$dedup", (object?)dedupKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static bool ObjectExists(SqliteConnection connection, string type, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = $type AND name = $name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
}
