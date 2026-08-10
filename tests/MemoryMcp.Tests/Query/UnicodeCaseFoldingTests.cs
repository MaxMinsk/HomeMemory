using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Query;

/// <summary>
/// MEMP-238: the filter DSL's <c>contains</c> is documented as a case-insensitive substring, but it compiled to
/// SQLite <c>LIKE</c>, whose folding covers ASCII A–Z only — so it worked for English keys and silently returned
/// nothing for the owner's Russian notes. These cover the folding that <c>mem_contains</c>/<c>mem_lower</c> now
/// provide. Non-ASCII text is assembled from code points so the source stays ASCII (English gate).
/// </summary>
public class UnicodeCaseFoldingTests
{
    private static string Chars(params int[] codePoints) => new(codePoints.Select(c => (char)c).ToArray());

    // "chili", lower-case and title-case.
    private static readonly string ChiliLower = Chars(0x0447, 0x0438, 0x043B, 0x0438);
    private static readonly string ChiliTitle = Chars(0x0427, 0x0438, 0x043B, 0x0438);

    // "creme brulee" with its accents, as stored and as shouted.
    private static readonly string CremeLower = Chars(0x0063, 0x0072, 0x00E8, 0x006D, 0x0065); // creme
    private static readonly string CremeUpper = Chars(0x0043, 0x0052, 0x00C8, 0x004D, 0x0045); // CREME
    private static readonly string BruleeLower = Chars(0x0062, 0x0072, 0x00FB, 0x006C, 0x00E9, 0x0065); // brulee
    private static readonly string BruleeUpper = Chars(0x0042, 0x0052, 0x00DB, 0x004C, 0x00C9, 0x0045); // BRULEE

    [Fact]
    public void Contains_folds_case_for_cyrillic()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("kitchen", "fact", $"Dried {ChiliTitle} peppers in stock", null, """{ "statement": "pantry" }""", null, "dried", "me");

        // The reported failure: the lower-case needle found nothing while the title-case one found the note.
        Assert.Single(repo.Search(domain: "kitchen", filter: $"title contains '{ChiliLower}'").Items);
        Assert.Single(repo.Search(domain: "kitchen", filter: $"title contains '{ChiliTitle}'").Items);
    }

    [Fact]
    public void Contains_folds_case_for_accented_latin()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var statement = $$"""{ "statement": "{{CremeLower}} {{BruleeLower}} for six" }""";
        repo.Upsert("kitchen", "fact", "Dessert", null, statement, null, "dessert", "me");

        Assert.Single(repo.Search(domain: "kitchen", filter: $"payload.statement contains '{BruleeUpper}'").Items);
        Assert.Single(repo.Search(domain: "kitchen", filter: $"payload.statement contains '{CremeUpper} {BruleeUpper}'").Items);
    }

    [Fact]
    public void Contains_takes_the_needle_literally()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "fact", "P", null, """{ "statement": "100% done" }""", null, "p", "me");
        repo.Upsert("development", "fact", "Q", null, """{ "statement": "1000 done" }""", null, "q", "me");

        // '%' and '_' were LIKE wildcards that had to be escaped; mem_contains has no pattern syntax at all.
        Assert.Equal("P", Assert.Single(repo.Search(domain: "development", filter: "payload.statement contains '100%'").Items).Title);
        Assert.Empty(repo.Search(domain: "development", filter: "payload.statement contains '10_0'").Items);
    }

    [Fact]
    public void Contains_still_reads_a_non_text_payload_value_as_text()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "memory_rule", "top", null, """{ "description": "a", "priority": 10 }""", null, "r-top", "me");
        repo.Upsert("development", "memory_rule", "low", null, """{ "description": "b", "priority": 2 }""", null, "r-low", "me");

        // priority is a JSON number; LIKE coerced it to text, and mem_contains keeps that rather than throwing.
        Assert.Equal("top", Assert.Single(repo.Search(domain: "development", filter: "payload.priority contains '10'").Items).Title);
    }

    [Fact]
    public void Suggest_capture_sees_a_cyrillic_title_that_differs_only_in_case()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("kitchen", "fact", ChiliTitle, "a pantry note", """{ "statement": "pantry" }""", null, "chili", "me");

        // The duplicate probe compared lower(title): ASCII-only, so the same Russian title in another case
        // read as a new note and the agent was told to capture a duplicate.
        var suggestion = repo.SuggestCapture("kitchen", "fact", ChiliLower, "a different body", null, null, null);

        Assert.Equal("update", suggestion.Action);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
