using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NoteEventSinkTests
{
    private const string ValidPayload = """{ "key": "MEMP-100", "status": "ready" }""";

    [Fact]
    public void Upsert_create_emits_create_event_with_facets()
    {
        using var temp = new TempDatabase();
        var sink = new FakeNoteEventSink();
        var repo = NewRepo(temp, sink);

        repo.Upsert("memory-mcp", "backlog_item", "Title", "secret body", ValidPayload,
            """["x", "y"]""", dedupKey: "MEMP-100", sourceAgent: "tester", project: "alpha");

        var e = Assert.Single(sink.Events);
        Assert.Equal("create", e.Op);
        Assert.Equal("memory-mcp", e.Domain);
        Assert.Equal("backlog_item", e.Type);
        Assert.Equal("alpha", e.Project);
        Assert.Equal(new[] { "x", "y" }, e.Tags);
        Assert.False(string.IsNullOrEmpty(e.Id));
        Assert.False(string.IsNullOrEmpty(e.Ts));
    }

    [Fact]
    public void Upsert_update_emits_update_event()
    {
        using var temp = new TempDatabase();
        var sink = new FakeNoteEventSink();
        var repo = NewRepo(temp, sink);

        repo.Upsert("memory-mcp", "backlog_item", "First", null, ValidPayload, null, "MEMP-100", "a");
        repo.Upsert("memory-mcp", "backlog_item", "Second", null,
            """{ "key": "MEMP-100", "status": "in_progress" }""", null, "MEMP-100", "b");

        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("create", sink.Events[0].Op);
        Assert.Equal("update", sink.Events[1].Op);
        Assert.Equal(sink.Events[0].Id, sink.Events[1].Id);
    }

    [Fact]
    public void Supersede_emits_supersede_event_for_old_note()
    {
        using var temp = new TempDatabase();
        var sink = new FakeNoteEventSink();
        var repo = NewRepo(temp, sink);

        var oldNote = repo.Upsert("memory-mcp", "backlog_item", "Old", null, ValidPayload, """["t"]""", "MEMP-100", "a");
        var newNote = repo.Upsert("memory-mcp", "backlog_item", "New", null,
            """{ "key": "MEMP-101", "status": "ready" }""", null, "MEMP-101", "b");
        sink.Events.Clear();

        Assert.True(repo.Supersede(oldNote.Id, newNote.Id));

        var e = Assert.Single(sink.Events);
        Assert.Equal("supersede", e.Op);
        Assert.Equal(oldNote.Id, e.Id);
        Assert.Equal("backlog_item", e.Type);
        Assert.Equal(new[] { "t" }, e.Tags);
    }

    [Fact]
    public void Archive_emits_archive_event()
    {
        using var temp = new TempDatabase();
        var sink = new FakeNoteEventSink();
        var repo = NewRepo(temp, sink);

        var note = repo.Upsert("memory-mcp", "backlog_item", "Title", null, ValidPayload, null, "MEMP-100", "a");
        sink.Events.Clear();

        Assert.True(repo.Archive(note.Id));

        var e = Assert.Single(sink.Events);
        Assert.Equal("archive", e.Op);
        Assert.Equal(note.Id, e.Id);
    }

    private static NotesRepository NewRepo(TempDatabase temp, INoteEventSink sink)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources(), timeProvider: null, eventSink: sink);
    }

    private sealed class FakeNoteEventSink : INoteEventSink
    {
        public List<NoteChangedEvent> Events { get; } = new();

        public void Publish(NoteChangedEvent e) => Events.Add(e);
    }
}
