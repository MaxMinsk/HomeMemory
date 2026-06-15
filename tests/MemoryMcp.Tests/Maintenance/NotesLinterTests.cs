using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Maintenance;

public class NotesLinterTests
{
    [Fact]
    public void Flags_missing_tags_dedup_key_and_title()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        // No title, no body, no tags, no dedupKey — three issues on one note.
        repo.Upsert("home", "backlog_item", null, null, """{ "key": "MEMP-200", "status": "ready" }""", null, null, "me");

        var findings = new NotesLinter(factory).Lint(domain: null, restrictToDomains: null);

        Assert.Contains(findings, f => f.Rule == "no_tags");
        Assert.Contains(findings, f => f.Rule == "no_dedup_key");
        Assert.Contains(findings, f => f.Rule == "no_title");
    }

    [Fact]
    public void Clean_note_produces_no_findings()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("home", "backlog_item", "Tidy", null, """{ "key": "MEMP-201", "status": "ready" }""", """["t"]""", "MEMP-201", "me");

        Assert.Empty(new NotesLinter(factory).Lint(domain: null, restrictToDomains: null));
    }

    [Fact]
    public void Flags_dangling_link()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var id = repo.Upsert("home", "backlog_item", "A", null, """{ "key": "MEMP-202", "status": "ready" }""", """["t"]""", "MEMP-202", "me").Id;

        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO note_links (from_id, to_id, rel, created_utc) VALUES ($f, 'ghost', 'depends_on', '2026-01-01T00:00:00Z');";
            command.Parameters.AddWithValue("$f", id);
            command.ExecuteNonQuery();
        }

        var broken = Assert.Single(new NotesLinter(factory).Lint(domain: null, restrictToDomains: null), f => f.Rule == "broken_link");
        Assert.Equal(id, broken.NoteId);
    }

    [Fact]
    public void Scope_restriction_limits_findings_to_allowed_domains()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("home", "backlog_item", null, null, """{ "key": "MEMP-203", "status": "ready" }""", null, null, "me");
        repo.Upsert("work", "backlog_item", null, null, """{ "key": "WORK-200", "status": "ready" }""", null, null, "me");

        var homeOnly = new NotesLinter(factory).Lint(domain: null, restrictToDomains: new[] { "home" });
        Assert.All(homeOnly, f => Assert.Equal("home", f.Domain));

        Assert.Empty(new NotesLinter(factory).Lint(domain: null, restrictToDomains: Array.Empty<string>()));
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return (new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()), factory);
    }
}
