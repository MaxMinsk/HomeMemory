using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-214: recall is lean by default (snippet + identity, no full payload) and caps linked neighbors, so a
// context block doesn't flood the agent's window.
public class NotesRecallLeanTests
{
    [Fact]
    public void Recall_omits_payload_by_default_but_keeps_identity()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "fact", "Alpha note", "alpha gizmo detail",
            """{ "statement": "a large payload value we do not want dumped into context" }""", null, "alpha-key", "me", project: "widget-lab");

        var lean = repo.Recall("alpha", "development", 10, restrictToDomains: null).Hits;

        Assert.NotEmpty(lean);
        Assert.All(lean, h => Assert.Null(h.PayloadJson));   // payload dropped
        Assert.All(lean, h => Assert.Null(h.TagsJson));      // tags dropped
        var hit = lean.Single(h => h.DedupKey == "alpha-key");
        Assert.Equal("Alpha note", hit.Title);               // identity kept
        Assert.Equal("widget-lab", hit.Project);
        Assert.False(string.IsNullOrEmpty(hit.Snippet));     // relevance preserved
    }

    [Fact]
    public void Recall_includes_payload_when_asked()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "fact", "Alpha note", "alpha gizmo detail",
            """{ "statement": "the payload we DO want on a board view" }""", null, "alpha-key", "me");

        var full = repo.Recall("alpha", "development", 10, restrictToDomains: null, includePayload: true).Hits;

        Assert.Contains(full, h => h.PayloadJson is not null && h.PayloadJson.Contains("board view", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Recall_caps_neighbors()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var hub = repo.Upsert("development", "fact", "Hub", "hubword central", """{ "statement": "x" }""", null, "hub", "me").Id;
        for (var i = 0; i < 8; i++)
        {
            var leaf = repo.Upsert("development", "fact", $"Leaf {i}", "leaf", """{ "statement": "y" }""", null, $"leaf-{i}", "me").Id;
            repo.Link(hub, leaf, "relates_to");
        }

        var capped = repo.Recall("hubword", "development", 10, restrictToDomains: null, maxNeighbors: 3);

        Assert.True(capped.Neighbors.Count <= 3, $"expected <= 3 neighbors, got {capped.Neighbors.Count}");
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
