using System.Linq;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-202 (type-aware no_tags) + MEMP-200 (orphan_note connectivity rule).
public class NotesLinterProfileTests
{
    [Fact]
    public void No_tags_skips_types_found_by_key_not_tag()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = New(temp);
        var sprint = repo.Upsert("development", "sprint", "S99", null, """{ "key": "S99", "goal": "x", "status": "planned" }""", null, "S99", "me").Id;
        var fact = repo.Upsert("kitchen", "fact", "Untagged fact", "body", """{ "statement": "x" }""", null, "fact-untagged", "me").Id;

        var findings = new NotesLinter(factory).Lint(null, null, 1000);

        Assert.Contains(findings, f => f.Rule == "no_tags" && f.NoteId == fact);        // ordinary note still flagged
        Assert.DoesNotContain(findings, f => f.Rule == "no_tags" && f.NoteId == sprint); // sprint is exempt
    }

    [Fact]
    public void Orphan_note_flags_an_old_unlinked_knowledge_note_only()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = New(temp);
        var orphan = repo.Upsert("kitchen", "fact", "Lonely fact", "b", """{ "statement": "alone" }""", null, "fact-orphan", "me").Id;
        var linkedA = repo.Upsert("kitchen", "fact", "Linked A", "b", """{ "statement": "a" }""", null, "fact-a", "me").Id;
        var linkedB = repo.Upsert("kitchen", "fact", "Linked B", "b", """{ "statement": "b" }""", null, "fact-b", "me").Id;
        repo.Link(linkedA, linkedB, "relates_to");
        var journal = repo.AppendJournal("kitchen", "a fleeting unlinked thought", null, null, "me");

        // Clock far in the future so every note is well past the 30-day orphan age window.
        var clock = new FakeTimeProvider(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var findings = new NotesLinter(factory, clock).Lint(null, null, 1000);

        Assert.Contains(findings, f => f.Rule == "orphan_note" && f.NoteId == orphan);
        Assert.DoesNotContain(findings, f => f.Rule == "orphan_note" && f.NoteId == linkedA); // has a link
        Assert.DoesNotContain(findings, f => f.Rule == "orphan_note" && f.NoteId == linkedB); // has a link
        Assert.DoesNotContain(findings, f => f.Rule == "orphan_note" && f.NoteId == journal); // journal is exempt
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) New(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return (new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()), factory);
    }
}
