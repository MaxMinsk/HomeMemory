using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-179/180/181/182/183: CAS on upsert, bulk upsert/link, richer conflict info, idempotent no-op.
public class NotesConcurrencyTests
{
    private const string Payload = """{ "key": "MEMP-100", "status": "ready" }""";

    [Fact]
    public void Upsert_with_matching_expectedRevision_updates()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var first = repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a");

        var second = repo.Upsert("memory-mcp", "backlog_item", "Title", "new body", Payload, null, "MEMP-100", "b",
            expectedRevision: first.UpdatedUtc);

        Assert.False(second.Created);
        Assert.NotEqual(first.UpdatedUtc, second.UpdatedUtc); // revision advanced
    }

    [Fact]
    public void Upsert_with_stale_expectedRevision_conflicts_and_writes_nothing()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var first = repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a");
        repo.Upsert("memory-mcp", "backlog_item", "Title", "moved on", Payload, null, "MEMP-100", "b"); // someone else writes

        var conflict = Assert.Throws<ConcurrencyException>(() =>
            repo.Upsert("memory-mcp", "backlog_item", "Title", "my edit", Payload, null, "MEMP-100", "c",
                expectedRevision: first.UpdatedUtc));

        Assert.NotNull(conflict.CurrentRevision);
        Assert.Equal("b", conflict.CurrentSourceAgent); // the last writer is reported
        Assert.Contains("body", conflict.ChangedFields); // my write would change the body
        Assert.Equal("moved on", repo.Get(first.Id)!.Body); // untouched by the failed write
    }

    [Fact]
    public void Upsert_with_expectedRevision_but_no_note_conflicts()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        Assert.Throws<ConcurrencyException>(() =>
            repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a",
                expectedRevision: "2020-01-01T00:00:00.0000000Z"));
    }

    [Fact]
    public void Reupserting_identical_content_is_a_quiet_noop()
    {
        using var temp = new TempDatabase();
        var sink = new FakeSink();
        var repo = NewRepo(temp, sink);
        var first = repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a");
        sink.Events.Clear();

        var again = repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a");

        Assert.True(again.Unchanged);
        Assert.False(again.Created);
        Assert.Equal(first.UpdatedUtc, again.UpdatedUtc); // revision NOT bumped
        Assert.Empty(sink.Events);                        // no change event / MQTT publish
        Assert.Single(repo.Events(first.Id));             // no new audit row beyond the create
    }

    [Fact]
    public void A_real_change_after_a_noop_still_writes_and_emits()
    {
        using var temp = new TempDatabase();
        var sink = new FakeSink();
        var repo = NewRepo(temp, sink);
        repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a");
        repo.Upsert("memory-mcp", "backlog_item", "Title", "body", Payload, null, "MEMP-100", "a"); // no-op
        sink.Events.Clear();

        var changed = repo.Upsert("memory-mcp", "backlog_item", "Title", "different", Payload, null, "MEMP-100", "a");

        Assert.False(changed.Unchanged);
        var e = Assert.Single(sink.Events);
        Assert.Equal("update", e.Op);
    }

    [Fact]
    public void UpsertMany_commits_the_whole_batch()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var inputs = new List<NoteUpsertInput>
        {
            new("memory-mcp", "backlog_item", "One", null, """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201"),
            new("memory-mcp", "backlog_item", "Two", null, """{ "key": "MEMP-202", "status": "ready" }""", null, "MEMP-202"),
            new("memory-mcp", "backlog_item", "Three", null, """{ "key": "MEMP-203", "status": "ready" }""", null, "MEMP-203"),
        };

        var results = repo.UpsertMany(inputs, "tester");

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Created));
        Assert.Equal(3, repo.Search(domain: "memory-mcp", type: "backlog_item").Items.Count);
    }

    [Fact]
    public void UpsertMany_rolls_the_whole_batch_back_on_one_bad_item()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var inputs = new List<NoteUpsertInput>
        {
            new("memory-mcp", "backlog_item", "Good", null, """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201"),
            new("memory-mcp", "backlog_item", "Bad", null, """{ "status": "ready" }""", null, "MEMP-202"), // missing required key
        };

        var error = Assert.Throws<NoteValidationException>(() => repo.UpsertMany(inputs, "tester"));

        Assert.Contains("item[1]", error.Message); // the offending index is named
        Assert.Empty(repo.Search(domain: "memory-mcp", type: "backlog_item").Items); // nothing persisted
    }

    [Fact]
    public void LinkMany_creates_all_links_atomically_and_is_idempotent()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t").Id;
        var b = repo.Upsert("memory-mcp", "backlog_item", "B", null, """{ "key": "MEMP-202", "status": "ready" }""", null, "MEMP-202", "t").Id;
        var c = repo.Upsert("memory-mcp", "backlog_item", "C", null, """{ "key": "MEMP-203", "status": "ready" }""", null, "MEMP-203", "t").Id;

        var first = repo.LinkMany(new List<LinkInput> { new(a, b, "depends_on"), new(a, c, "depends_on") });
        Assert.Equal(2, first.Created);
        Assert.Equal(0, first.AlreadyPresent);

        var again = repo.LinkMany(new List<LinkInput> { new(a, b, "depends_on"), new(a, c, "relates_to") });
        Assert.Equal(1, again.Created);          // only the new rel is created
        Assert.Equal(1, again.AlreadyPresent);   // the duplicate is a no-op
    }

    [Fact]
    public void LinkMany_rolls_back_when_an_endpoint_is_missing()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t").Id;
        var b = repo.Upsert("memory-mcp", "backlog_item", "B", null, """{ "key": "MEMP-202", "status": "ready" }""", null, "MEMP-202", "t").Id;

        var error = Assert.Throws<AssembleException>(() =>
            repo.LinkMany(new List<LinkInput> { new(a, b, "depends_on"), new(a, "missing-id", "depends_on") }));

        Assert.Contains("link[1]", error.Message);
        Assert.Empty(repo.Links(a)); // the first link was rolled back too
    }

    private static NotesRepository NewRepo(TempDatabase temp, INoteEventSink? sink = null)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources(), timeProvider: null, eventSink: sink);
    }

    private sealed class FakeSink : INoteEventSink
    {
        public List<NoteChangedEvent> Events { get; } = new();

        public void Publish(NoteChangedEvent e) => Events.Add(e);
    }
}
