using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesReadMutateTests
{
    [Fact]
    public void Get_returns_full_note()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", title: "Title", body: "Body", tags: """["x"]""");

        var note = repo.Get(id);

        Assert.NotNull(note);
        Assert.Equal("memory-mcp", note!.Domain);
        Assert.Equal("backlog_item", note.Type);
        Assert.Equal("Title", note.Title);
        Assert.Equal("active", note.Status);
        Assert.Contains("MEMP-200", note.PayloadJson!);
    }

    [Fact]
    public void Get_returns_null_when_missing()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);

        Assert.Null(repo.Get("nope"));
    }

    [Fact]
    public void Search_by_text_returns_snippet_without_body()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "findme uniqueword");

        var hits = repo.Search(query: "uniqueword");

        var hit = Assert.Single(hits.Items);
        Assert.Equal(id, hit.Id);
        Assert.NotNull(hit.Snippet);
        Assert.Contains("uniqueword", hit.Snippet!);
    }

    [Fact]
    public void Search_handles_fts_special_characters_without_error()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready", body: "alpha beta");

        // Hyphen/colon/quote would break a raw FTS5 MATCH; the sanitizer turns them into phrases.
        Assert.Single(repo.Search(query: "alpha-beta").Items);
        Assert.Empty(repo.Search(query: "\"missing\":").Items);
    }

    [Fact]
    public void Search_filters_by_domain_and_type()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready", domain: "memory-mcp");
        Seed(repo, "WORK-200", "ready", domain: "work");

        Assert.Single(repo.Search(domain: "memory-mcp").Items);
        Assert.Single(repo.Search(domain: "work").Items);
        Assert.Empty(repo.Search(type: "recipe").Items);
    }

    [Fact]
    public void Search_filters_by_tag()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready", tags: """["urgent"]""");
        Seed(repo, "MEMP-201", "ready", tags: """["later"]""");

        Assert.Single(repo.Search(tags: new[] { "urgent" }).Items);
        Assert.Empty(repo.Search(tags: new[] { "missing" }).Items);
    }

    [Fact]
    public void Search_without_query_returns_structured_results()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready");
        Seed(repo, "MEMP-201", "ready");

        var hits = repo.Search();

        Assert.Equal(2, hits.Total);
        Assert.Equal(2, hits.Items.Count);
        Assert.All(hits.Items, h => Assert.Null(h.Snippet));
    }

    [Fact]
    public void Search_paginates_with_total_and_hasMore()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        for (var i = 0; i < 5; i++)
        {
            Seed(repo, $"MEMP-30{i}", "ready");
        }

        var first = repo.Search(limit: 2, offset: 0);
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);

        var last = repo.Search(limit: 2, offset: 4);
        Assert.Single(last.Items);
        Assert.False(last.HasMore);

        var clamped = repo.Search(limit: 9999);
        Assert.Equal(NotesReader.MaxLimit, clamped.Limit); // page size clamped to 100
        Assert.Equal(5, clamped.Items.Count);
    }

    [Fact]
    public void Archive_hides_from_default_search_but_get_and_archived_search_work()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "findme");

        Assert.True(repo.Archive(id));

        Assert.Empty(repo.Search(query: "findme").Items);                    // default status = active
        Assert.Single(repo.Search(query: "findme", status: "archived").Items);
        Assert.Equal("archived", repo.Get(id)!.Status);               // still retrievable by id
        Assert.False(repo.Archive(id));                               // already archived -> no-op
    }

    [Fact]
    public void Link_inserts_link_and_event()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready");
        var b = Seed(repo, "MEMP-201", "ready");

        repo.Link(a, b, "depends_on");

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_links WHERE from_id = '{a}' AND to_id = '{b}' AND rel = 'depends_on';"));
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_events WHERE note_id = '{a}' AND op = 'link';"));
    }

    [Fact]
    public void Supersede_marks_old_and_links_new()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var oldId = Seed(repo, "MEMP-200", "ready");
        var newId = Seed(repo, "MEMP-201", "ready");

        Assert.True(repo.Supersede(oldId, newId));

        Assert.Equal("superseded", repo.Get(oldId)!.Status);
        using var c = factory.Create();
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_links WHERE from_id = '{newId}' AND to_id = '{oldId}' AND rel = 'supersedes';"));
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_events WHERE note_id = '{oldId}' AND op = 'supersede';"));
    }

    [Fact]
    public void Search_filter_dsl_matches_payload_field()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        SeedSprint(repo, "MEMP-700", "S1");
        SeedSprint(repo, "MEMP-701", "S1");
        SeedSprint(repo, "MEMP-702", "S2");

        var s1 = repo.Search(type: "backlog_item", filter: "payload.sprint == 'S1'");
        Assert.Equal(2, s1.Total);

        var combined = repo.Search(filter: "payload.sprint == 'S1' AND payload.status == 'ready'");
        Assert.Equal(2, combined.Total);

        Assert.Empty(repo.Search(filter: "payload.sprint == 'S9'").Items);
    }

    private static string SeedSprint(NotesRepository repo, string key, string sprint) =>
        repo.Upsert("memory-mcp", "backlog_item", key, null,
            $$"""{ "key": "{{key}}", "status": "ready", "sprint": "{{sprint}}" }""", null, key, "tester").Id;

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return (new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()), factory);
    }

    private static string Seed(NotesRepository repo, string key, string status,
        string domain = "memory-mcp", string? title = null, string? body = null, string? tags = null)
    {
        var payload = $$"""{ "key": "{{key}}", "status": "{{status}}" }""";
        return repo.Upsert(domain, "backlog_item", title ?? key, body, payload, tags, key, "tester").Id;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
