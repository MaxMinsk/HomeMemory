using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Storage;

public class FtsSearchTests
{
    [Fact]
    public void Fts_migration_brings_schema_to_latest()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        var migrator = new Migrator(factory, SchemaMigrations.All);

        Assert.Equal(migrator.LatestVersion, migrator.Migrate());
        Assert.True(migrator.LatestVersion >= 2);
    }

    [Fact]
    public void Search_finds_by_title_body_and_tags()
    {
        using var temp = new TempDatabase();
        using var connection = Migrated(temp);

        Insert(connection, "borscht", title: "Borscht recipe", body: "beet cabbage broth", tags: "soup");
        Insert(connection, "tandoori", title: "Tandoori chicken", body: "yogurt spices", tags: "grill");

        Assert.Equal(new[] { "borscht" }, Search(connection, "beet"));      // body
        Assert.Equal(new[] { "tandoori" }, Search(connection, "tandoori")); // title
        Assert.Equal(new[] { "borscht" }, Search(connection, "soup"));      // tags
    }

    [Fact]
    public void Search_excludes_archived_and_deleted()
    {
        using var temp = new TempDatabase();
        using var connection = Migrated(temp);

        Insert(connection, "active", title: "alpha note", body: "", tags: "");
        Insert(connection, "archived", title: "alpha note", body: "", tags: "", status: "archived");
        Insert(connection, "deleted", title: "alpha note", body: "", tags: "", deleted: 1);

        Assert.Equal(new[] { "active" }, Search(connection, "alpha"));
    }

    [Fact]
    public void Update_reindexes_changed_text()
    {
        using var temp = new TempDatabase();
        using var connection = Migrated(temp);

        Insert(connection, "n", title: "original", body: "", tags: "");
        Execute(connection, "UPDATE notes SET title = 'replaced' WHERE id = 'n';");

        Assert.Empty(Search(connection, "original"));
        Assert.Equal(new[] { "n" }, Search(connection, "replaced"));
    }

    [Fact]
    public void Search_ranks_more_relevant_first()
    {
        using var temp = new TempDatabase();
        using var connection = Migrated(temp);

        Insert(connection, "weak", title: "x", body: "alpha", tags: "");
        Insert(connection, "strong", title: "alpha", body: "alpha alpha alpha", tags: "alpha");

        Assert.Equal(new[] { "strong", "weak" }, Search(connection, "alpha"));
    }

    private static SqliteConnection Migrated(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return factory.Create();
    }

    private static void Insert(SqliteConnection connection, string id, string title, string body, string tags,
        string status = "active", long deleted = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO notes (id, domain, type, title, body, tags_json, status, deleted, created_utc, updated_utc) " +
            "VALUES ($id, 'd', 't', $title, $body, $tags, $status, $deleted, '2026-06-15T00:00:00Z', '2026-06-15T00:00:00Z');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$tags", tags);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$deleted", deleted);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] Search(SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT notes.id FROM notes_fts " +
            "JOIN notes ON notes.rowid = notes_fts.rowid " +
            "WHERE notes_fts MATCH $q AND notes.deleted = 0 AND notes.status = 'active' " +
            "ORDER BY bm25(notes_fts);";
        command.Parameters.AddWithValue("$q", query);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids.ToArray();
    }
}
