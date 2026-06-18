using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-184/185/186: composite/webhook event sinks, saved searches, activity stats.
public class NotesReactivityTests
{
    [Fact]
    public void Composite_sink_fans_out_to_every_sink_even_when_one_throws()
    {
        var ok1 = new CountingSink();
        var ok2 = new CountingSink();
        var composite = new CompositeNoteEventSink(new INoteEventSink[] { ok1, new ThrowingSink(), ok2 });

        composite.Publish(new NoteChangedEvent("id", "d", "t", null, Array.Empty<string>(), "create", "2026-01-01T00:00:00Z"));

        Assert.Equal(1, ok1.Count); // a throwing sink in the middle doesn't stop the others
        Assert.Equal(1, ok2.Count);
    }

    [Fact]
    public void Webhook_signature_matches_a_known_hmac_sha256_vector()
    {
        // RFC-style test vector: HMAC-SHA256(key="key", msg="The quick brown fox jumps over the lazy dog").
        var sig = WebhookSignature.Compute("key", "The quick brown fox jumps over the lazy dog");
        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", sig);

        Assert.NotEqual(sig, WebhookSignature.Compute("other", "The quick brown fox jumps over the lazy dog")); // key-sensitive
    }

    [Fact]
    public void NoteEvent_json_carries_the_body_free_facets()
    {
        var json = NoteEventJson.Serialize(new NoteChangedEvent("n1", "memory-mcp", "fact", "proj", new[] { "x" }, "update", "2026-06-18T00:00:00Z"));
        Assert.Contains("\"id\":\"n1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"op\":\"update\"", json, StringComparison.Ordinal);
        Assert.Contains("\"project\":\"proj\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedSearchSpec_parses_fields_and_is_lenient()
    {
        var spec = SavedSearchSpec.Parse("""{ "name": "Open bugs", "query": "bug", "type": "backlog_item", "tags": ["area:ads"], "limit": 5 }""");
        Assert.Equal("Open bugs", spec.Name);
        Assert.Equal("bug", spec.Query);
        Assert.Equal("backlog_item", spec.Type);
        Assert.Equal(new[] { "area:ads" }, spec.Tags);
        Assert.Equal(5, spec.Limit);

        var empty = SavedSearchSpec.Parse("not json");
        Assert.Null(empty.Name);
        Assert.Null(empty.Query);
    }

    [Fact]
    public void Saved_search_type_validates_and_runs_via_its_stored_params()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "Fix the alpha bug", null, """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t");
        repo.Upsert("memory-mcp", "backlog_item", "Beta cleanup", null, """{ "key": "MEMP-202", "status": "ready" }""", null, "MEMP-202", "t");
        // The saved_search schema must accept this payload (built-in saved_search@1).
        var saved = repo.Upsert("memory-mcp", "saved_search", "Alpha view", null,
            """{ "name": "Alpha view", "query": "alpha", "type": "backlog_item" }""", null, "view-alpha", "t");
        Assert.True(saved.Created); // the built-in saved_search@1 schema accepted the payload

        var note = repo.GetByDedupKey("memory-mcp", "saved_search", "view-alpha");
        var spec = SavedSearchSpec.Parse(note!.PayloadJson);
        var hits = repo.Search(spec.Query, "memory-mcp", spec.Type, spec.Tags, "active", spec.Limit ?? 20, 0, null);

        Assert.Equal("Fix the alpha bug", Assert.Single(hits.Items).Title);
    }

    [Fact]
    public void Activity_counts_by_op_and_type_over_the_window()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "One", "body one", """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t");
        repo.Upsert("memory-mcp", "backlog_item", "Two", "body two", """{ "key": "MEMP-202", "status": "ready" }""", null, "MEMP-202", "t");
        repo.Upsert("memory-mcp", "backlog_item", "One", "changed body", """{ "key": "MEMP-201", "status": "ready" }""", null, "MEMP-201", "t"); // update

        var all = repo.Activity("memory-mcp", "2000-01-01T00:00:00.0000000Z", restrictToDomains: null);
        Assert.Equal(3, all.Total);
        Assert.Equal(2, all.ByOp["create"]);
        Assert.Equal(1, all.ByOp["update"]);
        Assert.Equal(3, all.ByType["backlog_item"]);

        var future = repo.Activity("memory-mcp", "2100-01-01T00:00:00.0000000Z", restrictToDomains: null);
        Assert.Equal(0, future.Total);

        var outOfScope = repo.Activity(null, "2000-01-01T00:00:00.0000000Z", restrictToDomains: Array.Empty<string>());
        Assert.Equal(0, outOfScope.Total); // empty scope sees nothing
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private sealed class CountingSink : INoteEventSink
    {
        public int Count { get; private set; }

        public void Publish(NoteChangedEvent e) => Count++;
    }

    private sealed class ThrowingSink : INoteEventSink
    {
        public void Publish(NoteChangedEvent e) => throw new InvalidOperationException("boom");
    }
}
