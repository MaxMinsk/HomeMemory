using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class JournalAndDiagnosticsTests
{
    [Fact]
    public void AppendJournal_inserts_schemaless_note()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());

        var id = repo.AppendJournal("kitchen", "bought mackerel, try smoking it", "me");

        var note = repo.Get(id);
        Assert.NotNull(note);
        Assert.Equal("journal", note!.Type);
        Assert.Equal("bought mackerel, try smoking it", note.Body);
        Assert.Null(note.PayloadJson);
        Assert.Equal(0, note.SchemaVer);
        Assert.Single(repo.Search(query: "mackerel", domain: "kitchen").Items);
    }

    [Fact]
    public void Diagnostics_reports_version_schemas_and_counts()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        var migrator = new Migrator(factory, SchemaMigrations.All);
        migrator.Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        new NotesRepository(factory, registry)
            .Upsert("memory-mcp", "backlog_item", "T", null, """{ "key": "MEMP-300", "status": "ready" }""", null, "MEMP-300", "me");

        var status = new DiagnosticsService(factory, registry).Snapshot();

        Assert.Equal(migrator.LatestVersion, status.SchemaVersion);
        Assert.Contains("backlog_item@1", status.RegisteredSchemas);
        Assert.Equal(1, status.NoteCount);
        Assert.Contains("fts5", status.SearchBackend);
    }
}
