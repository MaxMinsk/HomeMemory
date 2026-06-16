using System.Text;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
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

        var id = repo.AppendJournal("kitchen", "bought mackerel, try smoking it", sourceAgent: "me");

        var note = repo.Get(id);
        Assert.NotNull(note);
        Assert.Equal("journal", note!.Type);
        Assert.Equal("bought mackerel, try smoking it", note.Body);
        Assert.Null(note.PayloadJson);
        Assert.Equal(0, note.SchemaVer);
        Assert.Equal("bought mackerel, try smoking it", note.Title);   // derived from the first line
        Assert.Equal("me", note.SourceAgent);
        Assert.NotNull(note.DedupKey);                                 // findable/editable
        Assert.Contains("unstructured", note.TagsJson!);               // marked for later structuring
        Assert.Single(repo.Search(query: "mackerel", domain: "kitchen").Items);
    }

    [Fact]
    public void AppendJournal_honors_given_title_and_tags_and_always_adds_unstructured()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());

        var id = repo.AppendJournal("kitchen", "line one\nline two", title: "Smoking plan", tagsJson: """["loc:summer-kitchen"]""", sourceAgent: "me");

        var note = repo.Get(id)!;
        Assert.Equal("Smoking plan", note.Title);
        Assert.Contains("loc:summer-kitchen", note.TagsJson!);
        Assert.Contains("unstructured", note.TagsJson!);
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
        Assert.Equal(1, status.NotesByType["backlog_item"]);   // breakdown by type
        Assert.Equal(0, status.AttachmentCount);
        Assert.Contains("fts5", status.SearchBackend);
    }

    [Fact]
    public void Diagnostics_counts_attachments_and_blob_bytes()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var blobs = new BlobStore(dir.Path, 0);
        new ArtifactsService(blobs, factory).Put("kitchen", Encoding.UTF8.GetBytes("hello"), "h.txt", "text/plain", null, "me");

        var status = new DiagnosticsService(factory, registry, blobs).Snapshot();

        Assert.Equal(1, status.AttachmentCount);
        Assert.Equal(5, status.BlobBytes);
    }

    [Fact]
    public void Diagnostics_reports_server_version_and_blob_quota()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var blobs = new BlobStore(dir.Path, 1_000_000);

        var status = new DiagnosticsService(factory, registry, blobs).Snapshot();

        Assert.Equal(1_000_000, status.BlobQuotaBytes);
        Assert.Matches(@"^\d+\.\d+\.\d+", status.ServerVersion); // semver from the assembly
    }

    [Fact]
    public void Diagnostics_reports_on_disk_db_size()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        new NotesRepository(factory, registry)
            .Upsert("memory-mcp", "backlog_item", "T", null, """{ "key": "MEMP-301", "status": "ready" }""", null, "MEMP-301", "me");

        var status = new DiagnosticsService(factory, registry).Snapshot();

        Assert.True(status.DbSizeBytes > 0, "a migrated database with a row should have a non-zero on-disk size");
    }

    [Fact]
    public void Capabilities_report_types_and_scope_for_a_restricted_token()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        var migrator = new Migrator(factory, SchemaMigrations.All);
        migrator.Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var diagnostics = new DiagnosticsService(factory, registry, new BlobStore(dir.Path, 1_000_000));

        var caps = diagnostics.Capabilities(RequestScope.ForDomains(new[] { "kitchen" }));

        Assert.Matches(@"^\d+\.\d+\.\d+", caps.ServerVersion);
        Assert.Equal(migrator.LatestVersion, caps.SchemaVersion);
        Assert.Equal("commons", caps.CommonsDomain);
        Assert.Equal(1_000_000, caps.BlobQuotaBytes);
        Assert.Contains(caps.Types, t => t.Type == "backlog_item" && t.Builtin && t.LatestVersion >= 1);

        Assert.False(caps.Scope.Unrestricted);
        Assert.Contains("kitchen", caps.Scope.ReadableDomains);
        Assert.Contains("commons", caps.Scope.ReadableDomains);     // commons is world-readable
        Assert.Contains("kitchen", caps.Scope.WritableDomains);
        Assert.DoesNotContain("commons", caps.Scope.WritableDomains); // read-shared, not write-shared
    }

    [Fact]
    public void Capabilities_marks_an_unrestricted_scope()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var diagnostics = new DiagnosticsService(factory, SchemaRegistry.FromEmbeddedResources());

        var caps = diagnostics.Capabilities(RequestScope.Unrestricted);

        Assert.True(caps.Scope.Unrestricted);
        Assert.Empty(caps.Scope.WritableDomains); // unrestricted => explicit lists are empty; the flag carries it
    }
}
