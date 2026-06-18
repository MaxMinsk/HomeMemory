using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>Guards payload full-text search (MEMP-120): values stored only inside a note's typed payload
/// — e.g. a recipe's <c>russian_name</c>/<c>name</c>/<c>aliases</c> — are found by <c>notes_search</c>,
/// including Cyrillic. Since MEMP-024 the stemmed sidecar also matches word forms (declensions), so payload
/// values are found across inflections. Cyrillic test words are built from code points so the source file
/// itself stays ASCII (English-gate clean).</summary>
public class PayloadFtsTests
{
    private static string Cyr(params int[] codePoints) => new(codePoints.Select(c => (char)c).ToArray());

    // borshch / ukrainskiy (masc.) / ukrainskaya (fem.) / klassicheskiy — assembled from Cyrillic code points.
    private static readonly string Borshch = Cyr(0x0431, 0x043E, 0x0440, 0x0449);
    private static readonly string UkrMasc = Cyr(0x0443, 0x043A, 0x0440, 0x0430, 0x0438, 0x043D, 0x0441, 0x043A, 0x0438, 0x0439);
    private static readonly string UkrFem = Cyr(0x0443, 0x043A, 0x0440, 0x0430, 0x0438, 0x043D, 0x0441, 0x043A, 0x0430, 0x044F);
    private static readonly string Klass = Cyr(0x043A, 0x043B, 0x0430, 0x0441, 0x0441, 0x0438, 0x0447, 0x0435, 0x0441, 0x043A, 0x0438, 0x0439);

    [Fact]
    public void Cyrillic_payload_value_is_full_text_searchable()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());

        // UkrMasc appears ONLY in the payload (statement) — not in title/body/tags.
        var payload = $$"""{ "statement": "{{Borshch}} {{UkrMasc}} {{Klass}}", "subject": "{{Borshch}}" }""";
        repo.Upsert("kitchen", "fact", "Notes", null, payload, null, "fact-borsch", "me");

        Assert.Single(repo.Search(query: UkrMasc, domain: "kitchen").Items);  // payload value is indexed
        Assert.Single(repo.Search(query: Borshch, domain: "kitchen").Items);  // another payload token
        Assert.Single(repo.Search(query: UkrFem, domain: "kitchen").Items);   // MEMP-024: declensions now match via the stemmed sidecar
    }

    [Fact]
    public void Payload_field_VALUES_are_searchable_but_field_NAMES_are_not()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());

        repo.Upsert("development", "backlog_item", "Task A", null, """{ "key": "MEMP-500", "status": "ready" }""", null, "MEMP-500", "me");

        // MEMP-152: a field NAME (key) no longer matches; a field VALUE still does.
        Assert.Empty(repo.Search(query: "status", domain: "development").Items);    // "status" is a key -> not indexed
        Assert.Single(repo.Search(query: "ready", domain: "development").Items);    // "ready" is a value -> found
        Assert.Single(repo.Search(query: "MEMP-500", domain: "development").Items); // a value (the key field) -> found
    }
}
