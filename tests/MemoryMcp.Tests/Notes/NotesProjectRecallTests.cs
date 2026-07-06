using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-209: project-aware recall — a requested project's notes are lifted via a soft RRF boost (cross-project
// hits still appear), and projectOnly hard-restricts the recall to that project.
public class NotesProjectRecallTests
{
    [Fact]
    public void Recall_lifts_the_requested_project_over_an_equally_relevant_other_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // Same title/body => equal lexical. 'other' is seeded LAST (newest) so recency opposes the project boost;
        // the boost must still lift the older same-project note above it.
        var mine = Seed(repo, "MEMP-900", "alpha context", "memory-mcp");
        var other = Seed(repo, "OTH-900", "alpha context", "other-proj");

        var boosted = repo.Recall("alpha context", "development", 10, restrictToDomains: null, project: "memory-mcp");

        Assert.Equal(mine, boosted.Hits[0].Id);                         // same-project ranks first
        Assert.Contains(boosted.Hits, h => h.Id == other);             // cross-project hit does NOT vanish
    }

    [Fact]
    public void Recall_without_a_project_does_not_favor_either_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-900", "alpha context", "memory-mcp");
        var other = Seed(repo, "OTH-900", "alpha context", "other-proj"); // newest

        var plain = repo.Recall("alpha context", "development", 10, restrictToDomains: null);

        Assert.Equal(other, plain.Hits[0].Id); // no project boost => recency wins, so the newest is first
    }

    [Fact]
    public void Recall_project_only_hard_restricts_to_the_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var mine = Seed(repo, "MEMP-900", "alpha context", "memory-mcp");
        Seed(repo, "OTH-900", "alpha context", "other-proj");

        var only = repo.Recall("alpha context", "development", 10, restrictToDomains: null, project: "memory-mcp", projectOnly: true);

        Assert.Single(only.Hits);
        Assert.Equal(mine, only.Hits[0].Id); // the other project is excluded entirely
    }

    [Fact]
    public void Project_signal_is_neutral_when_no_project_is_requested()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        Seed(repo, "MEMP-900", "alpha context", "memory-mcp");
        Seed(repo, "OTH-900", "alpha context", "other-proj");

        var items = repo.Search("alpha context", domain: "development", rank: "hybrid", explain: true).Items;

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(1, item.Explain!.ProjectRank)); // all tie at rank 1 => no effect
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string key, string title, string project)
    {
        var payload = $$"""{ "key": "{{key}}", "status": "ready" }""";
        return repo.Upsert("development", "backlog_item", title, title, payload, null, key, "tester", project).Id;
    }
}
