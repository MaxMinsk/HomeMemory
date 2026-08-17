using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-247: the box has to be able to say what the vector layer costs and how far its index has got, or every
/// decision about it stays an argument.
/// </summary>
public class EmbeddingLoadTests
{
    private sealed class StubEmbedder : IEmbedder
    {
        public string ModelId => "test/stub-v1";

        public int Dimensions => 2;

        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken = default) =>
            [.. texts.Select(_ => new[] { 1f, 0f })];
    }

    /// <summary>
    /// With the layer off this must report a clean "off", not throw and not look like a broken sensor. The
    /// add-on publishes these to Home Assistant every minute, where an exception would simply be silence.
    /// </summary>
    [Fact]
    public void With_the_layer_off_the_reading_is_off_rather_than_missing()
    {
        using var temp = new TempDatabase();
        var (diagnostics, _, _) = Build(temp, enabled: false);

        var load = diagnostics.EmbeddingLoad();

        Assert.False(load.Enabled);
        Assert.Null(load.Model);
        Assert.Equal(0, load.IndexedNotes);
        Assert.Equal(0, load.ActiveNotes);
    }

    /// <summary>
    /// Coverage is the figure that says whether semantic recall is answering from the whole corpus or part of
    /// it — a half-built index returns confident results from half the notes, and nothing else reveals that.
    /// </summary>
    [Fact]
    public void Index_coverage_counts_indexed_notes_against_the_active_corpus()
    {
        using var temp = new TempDatabase();
        var (diagnostics, recall, notes) = Build(temp, enabled: true);
        var first = notes.Upsert("kitchen", "fact", "Chili", "chili", """{ "statement": "a" }""", null, "a", "tester").Id;
        notes.Upsert("kitchen", "fact", "Kazan", "kazan", """{ "statement": "b" }""", null, "b", "tester");

        recall.Index(first, new NoteContent("fact", "Chili", "chili", null, """{ "statement": "a" }"""), "2026-08-17T00:00:00Z");
        var load = diagnostics.EmbeddingLoad();

        Assert.True(load.Enabled);
        Assert.Equal("test/stub-v1", load.Model);
        Assert.Equal(1, load.IndexedNotes);
        Assert.Equal(2, load.ActiveNotes);
        Assert.Equal(0, load.StalePassages);
    }

    /// <summary>
    /// The backend description used to be a constant claiming "no vectors" whatever was running, so an agent
    /// choosing how to phrase a query was told the opposite of the truth — and `status` and
    /// `memory_capabilities` have to agree, or neither can be believed.
    /// </summary>
    [Fact]
    public void The_reported_backend_follows_whether_vectors_are_actually_live()
    {
        using var temp = new TempDatabase();
        var (off, _, _) = Build(temp, enabled: false);
        var (on, _, _) = Build(temp, enabled: true);

        Assert.Contains("no vectors", off.Snapshot().SearchBackend, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no vectors", on.Snapshot().SearchBackend, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hybrid", on.Snapshot().SearchBackend, StringComparison.OrdinalIgnoreCase);
        // The two reports must not disagree about it either.
        Assert.Equal(on.Snapshot().SearchBackend, on.Capabilities(MemoryMcp.Core.Security.RequestScope.Unrestricted).SearchBackend);
    }

    private static (DiagnosticsService Diagnostics, VectorRecall Recall, NotesRepository Notes) Build(TempDatabase temp, bool enabled)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        var projector = new SchemaRetrievalProjector(registry);
        var passages = new PassageStore(factory);
        var recall = new VectorRecall(new StubEmbedder(), passages, projector, new EmbeddingOptions(enabled, "unused"));
        var diagnostics = new DiagnosticsService(factory, registry, null, recall, passages, projector);
        return (diagnostics, recall, new NotesRepository(factory, registry));
    }
}
