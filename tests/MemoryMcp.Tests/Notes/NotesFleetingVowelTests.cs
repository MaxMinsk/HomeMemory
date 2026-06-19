using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-192: RU fleeting-vowel morphology — a dictionary form (perec) now matches its inflections (perca/percev),
// which Snowball alone leaves on different stems (perec vs perc). Cyrillic is built from code points to keep ASCII.
public class NotesFleetingVowelTests
{
    private static readonly string Perec = P(0x43f, 0x435, 0x440, 0x435, 0x446);          // perec (nominative)
    private static readonly string Percev = P(0x43f, 0x435, 0x440, 0x446, 0x435, 0x432);  // percev (genitive plural)
    private static readonly string Perca = P(0x43f, 0x435, 0x440, 0x446, 0x430);          // perca (genitive singular)

    [Fact]
    public void Oblique_query_finds_a_note_stored_in_the_nominative()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", Perec + " variety", null,
            """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t");

        // The note stores "perec"; querying the inflected "percev"/"perca" still finds it (shared collapsed stem).
        Assert.Equal(1, repo.Search(query: Percev, domain: "memory-mcp", match: "all").Total);
        Assert.Equal(1, repo.Search(query: Perca, domain: "memory-mcp", match: "all").Total);
    }

    [Fact]
    public void Nominative_query_finds_a_note_stored_in_an_oblique_form()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Five " + Percev, null,
            """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t");

        Assert.Equal(1, repo.Search(query: Perec, domain: "memory-mcp", match: "all").Total);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string P(params int[] cp) => new(cp.Select(c => (char)c).ToArray());
}
