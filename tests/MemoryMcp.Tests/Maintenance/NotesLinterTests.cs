using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
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
    public void Flags_duplicate_notes_sharing_domain_type_title()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("home", "backlog_item", "Same Title", null, """{ "key": "MEMP-201", "status": "ready" }""", """["t"]""", "MEMP-201", "me");
        repo.Upsert("home", "backlog_item", "Same Title", null, """{ "key": "MEMP-202", "status": "ready" }""", """["t"]""", "MEMP-202", "me");
        repo.Upsert("home", "backlog_item", "Unique", null, """{ "key": "MEMP-203", "status": "ready" }""", """["t"]""", "MEMP-203", "me");

        var dups = new NotesLinter(factory).Lint(domain: null, restrictToDomains: null).Where(f => f.Rule == "duplicate").ToList();

        Assert.Equal(2, dups.Count); // both same-title notes, not the unique one
        Assert.All(dups, f => Assert.Equal("Same Title", f.Title));
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

    [Fact]
    public void Flags_unstructured_note_left_past_the_review_window()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var stale = repo.AppendJournal("home", "rough note kept unstructured", sourceAgent: "me"); // tagged 'unstructured'
        repo.AppendJournal("home", "fresh note, just added", sourceAgent: "me");                    // also unstructured, but recent
        BackdateNote(factory, stale, "2026-01-01T00:00:00.0000000Z");                                // 40+ days before "now"

        var findings = new NotesLinter(factory).Lint(domain: null, restrictToDomains: null);

        var hit = Assert.Single(findings, f => f.Rule == "stale_unstructured");
        Assert.Equal(stale, hit.NoteId); // only the backdated one, not the fresh note
    }

    [Theory]
    [InlineData("aws creds AKIAIOSFODNN7EXAMPLE in here")]
    [InlineData("token=ghp_abcdefgh1234567890ABCDEFGHIJKLMNOP")]
    [InlineData("api_key: sk-supersecretvalue12345")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void Flags_possible_secret_in_body(string body)
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.AppendJournal("home", body, sourceAgent: "me");

        var hit = Assert.Single(new NotesLinter(factory).Lint(domain: null, restrictToDomains: null), f => f.Rule == "possible_secret");
        Assert.DoesNotContain("AKIA", hit.Message, StringComparison.Ordinal); // the secret value is never echoed
    }

    [Fact]
    public void Ordinary_body_is_not_flagged_as_a_secret()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("home", "backlog_item", "Tidy", "buy mackerel and smoke it on Sunday",
            """{ "key": "MEMP-204", "status": "ready" }""", """["t"]""", "MEMP-204", "me");

        Assert.DoesNotContain(new NotesLinter(factory).Lint(domain: null, restrictToDomains: null), f => f.Rule == "possible_secret");
    }

    [Fact]
    public void Flags_a_note_not_verified_within_its_window()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("memory-mcp", "memory_rule", "Old rule", null,
            """{ "description": "x", "stale_after_days": 30, "last_verified_at": "2026-01-01" }""", """["r"]""", "rule-old", "me");
        repo.Upsert("memory-mcp", "memory_rule", "Fresh rule", null,
            """{ "description": "y", "stale_after_days": 30, "last_verified_at": "2026-06-10" }""", """["r"]""", "rule-fresh", "me");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero));

        var stale = new NotesLinter(factory, clock).Lint(domain: null, restrictToDomains: null)
            .Where(f => f.Rule == "stale_unverified").ToList();

        var hit = Assert.Single(stale);
        Assert.Equal("Old rule", hit.Title); // 167 days > 30; the fresh one (7 days) is not flagged
    }

    [Fact]
    public void Flags_oversized_note_without_a_heading_or_summary()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Big blob", new string('x', 5000), """{ "key": "MEMP-950", "status": "ready" }""", """["t"]""", "MEMP-950", "me");
        repo.Upsert("memory-mcp", "backlog_item", "Big structured", "# Heading\n" + new string('y', 5000), """{ "key": "MEMP-951", "status": "ready" }""", """["t"]""", "MEMP-951", "me");

        var hits = new NotesLinter(factory).Lint(domain: null, restrictToDomains: null)
            .Where(f => f.Rule == "oversized_no_summary").ToList();

        var hit = Assert.Single(hits);
        Assert.Equal("Big blob", hit.Title); // the one starting with '# Heading' is exempt
    }

    private static void BackdateNote(SqliteConnectionFactory factory, string id, string utc)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notes SET updated_utc = $u WHERE id = $id;";
        command.Parameters.AddWithValue("$u", utc);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return (new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()), factory);
    }
}
