using System.Collections.Generic;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-203: bulk notes_patch_many — many partial updates in one all-or-nothing transaction.
public class NotesPatchManyTests
{
    [Fact]
    public void Patches_many_notes_in_one_call()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = Seed(repo, "fact-a");
        var b = Seed(repo, "fact-b");

        var results = repo.PatchMany(new List<PatchInput>
        {
            new(a, TagsJson: """["retagged"]"""),
            new(b, TagsJson: """["retagged"]""", Title: "B renamed"),
        }, "tester");

        Assert.Equal(2, results.Count);
        Assert.Contains("retagged", repo.Get(a)!.TagsJson);
        Assert.Contains("retagged", repo.Get(b)!.TagsJson);
        Assert.Equal("B renamed", repo.Get(b)!.Title);
    }

    [Fact]
    public void A_missing_id_aborts_the_whole_batch()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = Seed(repo, "fact-a");

        Assert.ThrowsAny<Exception>(() => repo.PatchMany(new List<PatchInput>
        {
            new(a, TagsJson: """["would-change"]"""),
            new("does-not-exist", TagsJson: """["boom"]"""),
        }, "tester"));

        Assert.DoesNotContain("would-change", repo.Get(a)!.TagsJson ?? ""); // first item rolled back
    }

    [Fact]
    public void A_stale_revision_aborts_the_whole_batch()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = Seed(repo, "fact-a");

        Assert.Throws<ConcurrencyException>(() => repo.PatchMany(new List<PatchInput>
        {
            new(a, TagsJson: """["x"]""", ExpectedUpdatedUtc: "1999-01-01T00:00:00.0000000Z"),
        }, "tester"));
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string key) =>
        repo.Upsert("kitchen", "fact", key, "body", """{ "statement": "x" }""", null, key, "seed").Id;
}
