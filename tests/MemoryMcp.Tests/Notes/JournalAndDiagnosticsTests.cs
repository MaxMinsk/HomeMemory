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
    public void Diagnostics_note_counts_honor_the_callers_scope()
    {
        // MEMP-232: status/domains_list used to expose the whole corpus's shape (which domains exist and how
        // big they are) to a domain-scoped token, while search correctly hid it.
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var notes = new NotesRepository(factory, registry);
        notes.Upsert("kitchen", "backlog_item", "K", null, """{ "key": "KIT-300", "status": "ready" }""", null, "KIT-300", "me");
        notes.Upsert("work", "backlog_item", "W", null, """{ "key": "WORK-300", "status": "ready" }""", null, "WORK-300", "me");
        var diagnostics = new DiagnosticsService(factory, registry);

        var scoped = diagnostics.Snapshot(new[] { "kitchen" });

        Assert.Equal(1, scoped.NoteCount);
        Assert.Equal(new[] { "kitchen" }, scoped.NotesByDomain.Keys);
        Assert.Equal(1, scoped.NotesByType["backlog_item"]);
        Assert.Equal(1, scoped.NotesByStatus["active"]);

        Assert.Equal(2, diagnostics.Snapshot().NoteCount);                          // unrestricted still sees both
        Assert.Empty(diagnostics.Snapshot(Array.Empty<string>()).NotesByDomain);    // empty scope sees nothing
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

    /// <summary>
    /// MEMP-236: an unrestricted token used to get two EMPTY domain lists, which read as "you may reach nothing"
    /// but meant "you may reach everything" — indistinguishable to the caller, and it left `domains_list` as the
    /// only way to learn what exists. The lists now enumerate the domains that do exist; `unrestricted` is what
    /// says whether to read them as a limit or as an inventory.
    /// </summary>
    [Fact]
    public void Capabilities_lists_the_existing_domains_for_an_unrestricted_scope()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var notes = new NotesRepository(factory, registry);
        notes.Upsert("kitchen", "fact", "A note", "body", """{ "statement": "x" }""", null, "k1", "tester");
        notes.Upsert("development", "fact", "Another", "body", """{ "statement": "x" }""", null, "d1", "tester");

        var caps = new DiagnosticsService(factory, registry).Capabilities(RequestScope.Unrestricted);

        Assert.True(caps.Scope.Unrestricted);
        Assert.Equal(["development", "kitchen"], caps.Scope.ReadableDomains);
        Assert.Equal(["development", "kitchen"], caps.Scope.WritableDomains);
    }
}
