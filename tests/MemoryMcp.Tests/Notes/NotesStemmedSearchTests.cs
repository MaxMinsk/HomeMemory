using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesStemmedSearchTests
{
    [Fact]
    public void Search_matches_english_plural_forms_both_ways()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-400", "ANR investigation", "we saw an ANR spike on low-end devices");

        Assert.Equal(1, repo.Search(query: "ANRs", domain: "development").Total); // plural query finds the stored singular (stems)
        Assert.Equal(1, repo.Search(query: "ANR", domain: "development").Total);  // and the exact form still works (raw)
    }

    [Fact]
    public void Search_matches_russian_word_forms()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // Cyrillic via code points only (keep the source ASCII for the English gate): "задача" (nominative) vs "задаче".
        var nominative = new string(new[] { (char)0x0437, (char)0x0430, (char)0x0434, (char)0x0430, (char)0x0447, (char)0x0430 });
        var prepositional = new string(new[] { (char)0x0437, (char)0x0430, (char)0x0434, (char)0x0430, (char)0x0447, (char)0x0435 });
        Seed(repo, "MEMP-401", "RU note", nominative);

        Assert.Equal(1, repo.Search(query: prepositional, domain: "development").Total); // inflected query matches the stored form
    }

    [Fact]
    public void Search_keeps_ids_identifiers_and_paths_findable_verbatim()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-402", "Wiring", "call notes_upsert; see https://example.com/docs and src/Foo/Bar.cs for MEMP-024");

        Assert.Equal(1, repo.Search(query: "notes_upsert", domain: "development").Total); // identifier found verbatim (raw FTS)
        Assert.Equal(1, repo.Search(query: "MEMP-024", domain: "development").Total);     // a note ID found verbatim (raw FTS)
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static void Seed(NotesRepository repo, string key, string title, string body) =>
        repo.Upsert("development", "backlog_item", title, body, $$"""{ "key": "{{key}}", "status": "ready" }""", null, key, "tester");
}
