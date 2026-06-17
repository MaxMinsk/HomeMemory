using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesGraphTests
{
    [Fact]
    public void Graph_walks_links_breadth_first_with_hop_distances()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = Seed(repo, "memory-mcp", "A", "MEMP-300");
        var b = Seed(repo, "memory-mcp", "B", "MEMP-301");
        var c = Seed(repo, "memory-mcp", "C", "MEMP-302");
        var d = Seed(repo, "memory-mcp", "D", "MEMP-303");
        repo.Link(a, b, "depends_on");
        repo.Link(b, c, "depends_on");
        repo.Link(a, d, "uses");

        var oneHop = repo.Graph(a, maxHops: 1, restrictToDomains: null)!;
        Assert.Equal(3, oneHop.Nodes.Count);                         // A(0), B(1), D(1) — C is two hops out
        Assert.Equal(0, oneHop.Nodes.Single(n => n.Id == a).Hops);
        Assert.Equal(1, oneHop.Nodes.Single(n => n.Id == b).Hops);
        Assert.DoesNotContain(oneHop.Nodes, n => n.Id == c);
        Assert.Contains(oneHop.Edges, e => e.FromId == a && e.ToId == b && e.Rel == "depends_on");
        Assert.False(oneHop.Truncated);

        var twoHops = repo.Graph(a, maxHops: 2, restrictToDomains: null)!;
        Assert.Equal(4, twoHops.Nodes.Count);                        // C now reachable at hop 2
        Assert.Equal(2, twoHops.Nodes.Single(n => n.Id == c).Hops);
        Assert.Contains(twoHops.Edges, e => e.FromId == b && e.ToId == c);
    }

    [Fact]
    public void Graph_omits_neighbors_outside_the_caller_scope()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = Seed(repo, "memory-mcp", "A", "MEMP-310");
        var secret = Seed(repo, "kitchen", "Secret", "MEMP-311");
        repo.Link(a, secret, "mentions");

        var graph = repo.Graph(a, maxHops: 2, restrictToDomains: new[] { "memory-mcp" })!;

        Assert.DoesNotContain(graph.Nodes, n => n.Id == secret);     // out-of-scope neighbor dropped
        Assert.DoesNotContain(graph.Edges, e => e.ToId == secret);   // and so is the edge to it
    }

    [Fact]
    public void Graph_returns_null_for_a_root_outside_scope()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var a = Seed(repo, "kitchen", "A", "MEMP-320");
        Assert.Null(repo.Graph(a, maxHops: 1, restrictToDomains: new[] { "memory-mcp" }));
        Assert.Null(repo.Graph("missing", maxHops: 1, restrictToDomains: null));
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string domain, string title, string key) =>
        repo.Upsert(domain, "backlog_item", title, null, $$"""{ "key": "{{key}}", "status": "ready" }""", null, key, "tester").Id;
}
