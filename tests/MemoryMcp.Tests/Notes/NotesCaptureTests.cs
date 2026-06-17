using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesCaptureTests
{
    [Fact]
    public void SuggestCapture_skips_an_identical_note()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Cache work", "improve the caching layer", Payload("MEMP-660"), null, "MEMP-660", "me");

        var suggestion = repo.SuggestCapture("memory-mcp", "backlog_item", "Cache work", "improve the caching layer", Payload("MEMP-660"), null, null);

        Assert.Equal("skip", suggestion.Action);
        Assert.Contains(suggestion.Candidates, c => c.Why == "identical content");
    }

    [Fact]
    public void SuggestCapture_suggests_update_for_a_same_title_and_type_note()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Cache work", "old body", Payload("MEMP-661"), null, "MEMP-661", "me");

        var suggestion = repo.SuggestCapture("memory-mcp", "backlog_item", "Cache work", "a completely different body", Payload("MEMP-661"), null, null);

        Assert.Equal("update", suggestion.Action); // not identical, but same title+type -> update in place
        Assert.Contains(suggestion.Candidates, c => c.Why == "same title & type");
    }

    [Fact]
    public void SuggestCapture_asks_when_only_similar_notes_exist()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Caching guide", "how to cache effectively in the pipeline", Payload("MEMP-670"), null, "MEMP-670", "me");

        var suggestion = repo.SuggestCapture("memory-mcp", "backlog_item", "Pipeline caching ideas", "thoughts on caching the pipeline", null, null, null);

        Assert.Equal("ask", suggestion.Action); // different title, but the text overlaps
        Assert.Contains(suggestion.Candidates, c => c.Why == "text match");
    }

    [Fact]
    public void SuggestCapture_says_save_when_nothing_is_similar()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Caching guide", "how to cache the pipeline", Payload("MEMP-680"), null, "MEMP-680", "me");

        var suggestion = repo.SuggestCapture("memory-mcp", "backlog_item", "Unrelated zebra xylophone", "aardvark umbrella content", null, null, null);

        Assert.Equal("save", suggestion.Action);
        Assert.Empty(suggestion.Candidates);
    }

    [Fact]
    public void SuggestCapture_skips_an_empty_candidate()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var suggestion = repo.SuggestCapture("memory-mcp", "backlog_item", null, "  ", null, null, null);

        Assert.Equal("skip", suggestion.Action);
        Assert.Empty(suggestion.Candidates);
    }

    private static string Payload(string key) => $$"""{ "key": "{{key}}", "status": "ready" }""";

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
