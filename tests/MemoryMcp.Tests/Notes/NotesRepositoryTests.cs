using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesRepositoryTests
{
    private const string ValidPayload = """{ "key": "MEMP-100", "status": "ready" }""";

    [Fact]
    public void Upsert_creates_row_and_event_with_provenance()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        var result = repo.Upsert("memory-mcp", "backlog_item", "Title", "Body",
            ValidPayload, """["x"]""", dedupKey: "MEMP-100", sourceAgent: "tester");

        Assert.True(result.Created);
        Assert.False(string.IsNullOrEmpty(result.Id));

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM notes;"));
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM note_events WHERE op = 'create' AND actor = 'tester';"));
    }

    [Fact]
    public void Upsert_same_dedup_key_updates_in_place()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        var first = repo.Upsert("memory-mcp", "backlog_item", "First", null, ValidPayload, null, "MEMP-100", "a");
        var second = repo.Upsert("memory-mcp", "backlog_item", "Second", null,
            """{ "key": "MEMP-100", "status": "in_progress" }""", null, "MEMP-100", "b");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Id, second.Id);

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM notes;"));
        Assert.Equal("Second", (string)Scalar(c, "SELECT title FROM notes;")!);
        Assert.Equal(2L, Count(c, "SELECT count(*) FROM note_events;"));
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM note_events WHERE op = 'update';"));
    }

    [Fact]
    public void Upsert_rejects_invalid_payload_without_writing()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        Assert.Throws<NoteValidationException>(() =>
            repo.Upsert("memory-mcp", "backlog_item", "X", null,
                """{ "status": "bogus" }""", null, "MEMP-100", "a"));

        using var c = factory.Create();
        Assert.Equal(0L, Count(c, "SELECT count(*) FROM notes;"));
        Assert.Equal(0L, Count(c, "SELECT count(*) FROM note_events;"));
    }

    [Fact]
    public void Upsert_with_null_dedup_always_inserts()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-101", "status": "ready" }""", null, null, "a");
        repo.Upsert("memory-mcp", "backlog_item", "B", null, """{ "key": "MEMP-102", "status": "ready" }""", null, null, "a");

        using var c = factory.Create();
        Assert.Equal(2L, Count(c, "SELECT count(*) FROM notes;"));
    }

    [Fact]
    public void Update_preserves_created_utc_and_bumps_updated_utc()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        var (repo, factory) = NewRepo(temp, clock);

        repo.Upsert("memory-mcp", "backlog_item", "A", null, ValidPayload, null, "MEMP-100", "a");
        clock.Advance(TimeSpan.FromMinutes(5));
        repo.Upsert("memory-mcp", "backlog_item", "B", null,
            """{ "key": "MEMP-100", "status": "next" }""", null, "MEMP-100", "a");

        using var c = factory.Create();
        Assert.StartsWith("2026-06-15T10:00:00", (string)Scalar(c, "SELECT created_utc FROM notes;")!);
        Assert.StartsWith("2026-06-15T10:05:00", (string)Scalar(c, "SELECT updated_utc FROM notes;")!);
    }

    [Fact]
    public void Search_orders_by_a_numeric_payload_field_numerically()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("memory-mcp", "memory_rule", "low", null, """{ "description": "a", "priority": 2 }""", null, "r-low", "me");
        repo.Upsert("memory-mcp", "memory_rule", "top", null, """{ "description": "b", "priority": 10 }""", null, "r-top", "me");
        repo.Upsert("memory-mcp", "memory_rule", "mid", null, """{ "description": "c", "priority": 5 }""", null, "r-mid", "me");

        var page = repo.Search(type: "memory_rule", limit: 2, sort: "payload.priority desc");

        Assert.Equal("top", page.Items[0].Title); // 10 > 5 numerically (lexicographic would put "5" first)
        Assert.Equal("mid", page.Items[1].Title);
    }

    [Fact]
    public void Upsert_derives_envelope_project_from_payload_project()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = repo.Upsert("development", "backlog_item", "T", null,
            """{ "key": "MEMP-980", "status": "ready", "project": "unity-solitaire" }""", null, "MEMP-980", "me").Id;

        Assert.Equal("unity-solitaire", repo.Get(id)!.Project); // envelope project auto-set from payload.project
    }

    [Fact]
    public void ProjectBackfill_sets_envelope_project_from_payload_for_legacy_notes()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var id = repo.Upsert("development", "backlog_item", "T", null,
            """{ "key": "MEMP-981", "status": "ready", "project": "unity-solitaire" }""", null, "MEMP-981", "me").Id;
        using (var c = factory.Create()) // simulate a note written before the envelope axis existed
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE notes SET project = NULL WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Assert.Null(repo.Get(id)!.Project);

        Assert.Equal(1, ProjectBackfill.Run(factory, apply: false).NotesUpdated); // dry run counts it
        Assert.Equal(1, ProjectBackfill.Run(factory, apply: true).NotesUpdated);
        Assert.Equal("unity-solitaire", repo.Get(id)!.Project);
    }

    [Fact]
    public void Search_filters_by_the_project_envelope_axis()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("development", "backlog_item", "U", null, """{ "key": "MEMP-970", "status": "ready" }""", null, "MEMP-970", "me", project: "unity-solitaire");
        repo.Upsert("development", "backlog_item", "M", null, """{ "key": "MEMP-971", "status": "ready" }""", null, "MEMP-971", "me", project: "memory-mcp");

        var page = repo.Search(domain: "development", filter: "project == 'unity-solitaire'", includePayload: true);

        Assert.Equal("U", Assert.Single(page.Items).Title);
    }

    [Fact]
    public void Search_rejects_an_unsortable_or_injected_field()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Assert.Throws<FilterException>(() => repo.Search(sort: "evil; DROP TABLE notes"));
    }

    [Fact]
    public void CountByTypeInDomain_groups_active_notes_in_one_domain()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("kitchen", "backlog_item", "A", null, """{ "key": "MEMP-960", "status": "ready" }""", null, "MEMP-960", "me");
        repo.Upsert("kitchen", "memory_rule", "R", null, """{ "description": "x" }""", null, "rule-1", "me");
        repo.Upsert("home", "backlog_item", "B", null, """{ "key": "MEMP-961", "status": "ready" }""", null, "MEMP-961", "me");

        var counts = repo.CountByTypeInDomain("kitchen");

        Assert.Equal(2, counts.Count);            // only kitchen's types
        Assert.Equal(1, counts["backlog_item"]);  // home's backlog_item is not counted here
        Assert.Equal(1, counts["memory_rule"]);
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) NewRepo(TempDatabase temp, TimeProvider? clock = null)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources(), clock);
        return (repo, factory);
    }

    private static long Count(SqliteConnection connection, string sql) => Convert.ToInt64(Scalar(connection, sql));

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
