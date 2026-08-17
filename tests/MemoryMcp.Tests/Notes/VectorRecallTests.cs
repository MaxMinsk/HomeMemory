using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-196: the vector layer's wiring — indexing on write, best-passage scoring on read, and staying
/// completely inert while switched off.
/// <para>These use a deterministic stand-in embedder rather than the real model. That is on purpose: what is
/// under test here is the plumbing (are passages stored, is the best one used, does the flag really disable
/// everything), and a real model would make the assertions depend on its opinions. The model's own correctness
/// is pinned separately, against reference values, in the parity tests.</para>
/// </summary>
public class VectorRecallTests
{
    /// <summary>
    /// Maps text to a vector by counting a few marker words. Crude by design — it makes "which passage matched"
    /// decidable by reading the test, instead of trusting a black box.
    /// </summary>
    private sealed class MarkerEmbedder : IEmbedder
    {
        private static readonly string[] Markers = ["chili", "kazan", "sensor", "ventilation"];

        public string ModelId => "test/marker-v1";

        public int Dimensions => Markers.Length;

        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken = default)
        {
            var vectors = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                var vector = new float[Markers.Length];
                for (var i = 0; i < Markers.Length; i++)
                {
                    vector[i] = text.Contains(Markers[i], StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
                }

                var norm = (float)Math.Sqrt(vector.Sum(value => value * value));
                if (norm > 0)
                {
                    for (var i = 0; i < vector.Length; i++)
                    {
                        vector[i] /= norm;
                    }
                }

                vectors.Add(vector);
            }

            return vectors;
        }
    }

    [Fact]
    public void Indexing_stores_a_passage_per_projected_unit_and_scoring_finds_the_best()
    {
        using var temp = new TempDatabase();
        var (recall, store, notes) = NewRecall(temp, enabled: true);
        var id = Seed(notes, "n1", "Kazan", "a heavy pot for chili and stews");

        recall.Index(id, new NoteContent("fact", "Kazan", "a heavy pot for chili and stews", null, null), "2026-08-17T00:00:00Z");

        Assert.NotEmpty(store.ForScoring("test/marker-v1", MappingHashes(), [id]));
        Assert.True(recall.Score("chili", [id])[id].Score > 0, "a note mentioning the query term should score above zero");
    }

    /// <summary>
    /// The point of chunking: a note is relevant when ANY part of it is, so scoring takes the best passage.
    /// Averaging would dilute a precise match into the note's general topic — the same failure as embedding a
    /// whole note as one vector, which measured worse than indexing the title alone.
    /// </summary>
    [Fact]
    public void A_note_scores_as_its_best_passage_not_its_average()
    {
        using var temp = new TempDatabase();
        var (recall, _, notes) = NewRecall(temp, enabled: true);
        // The title mentions nothing; one buried window is squarely about the query.
        var body = new string('x', 400) + " sensor sensor sensor " + new string('y', 400);
        var id = Seed(notes, "n1", "Notes", body);
        recall.Index(id, new NoteContent("fact", "Notes", body, null, null), "2026-08-17T00:00:00Z");

        var score = recall.Score("sensor", [id])[id].Score;

        Assert.True(score > 0.9, $"the matching window should dominate, not be averaged away (got {score})");
    }

    [Fact]
    public void Rewriting_a_note_replaces_its_passages_rather_than_accumulating_them()
    {
        using var temp = new TempDatabase();
        var (recall, store, notes) = NewRecall(temp, enabled: true);
        var id = Seed(notes, "n1", "About chili", "chili chili");
        recall.Index(id, new NoteContent("fact", "About chili", "chili chili", null, null), "2026-08-17T00:00:00Z");
        var first = store.ForScoring("test/marker-v1", MappingHashes(), [id]).Count;

        recall.Index(id, new NoteContent("fact", "About sensors", "sensor sensor", null, null), "2026-08-17T01:00:00Z");

        Assert.Equal(first, store.ForScoring("test/marker-v1", MappingHashes(), [id]).Count);
        Assert.True(recall.Score("sensor", [id])[id].Score > 0, "the new content should be searchable");
        // The note still HAS vector evidence, so it is still scored — the evidence just says "unrelated" now.
        // That is a different claim from having no evidence at all, and the two stay distinguishable.
        Assert.Equal(0d, recall.Score("chili", [id])[id].Score, 6);
    }

    /// <summary>
    /// A note the index has not reached is ABSENT from the scores, not scored zero. "Not indexed yet" and
    /// "indexed and unrelated" are different claims, and only the ranker may decide what to do with either —
    /// conflating them would bury everything a partial rebuild has not covered.
    /// </summary>
    [Fact]
    public void An_unindexed_note_is_absent_from_the_scores()
    {
        using var temp = new TempDatabase();
        var (recall, _, notes) = NewRecall(temp, enabled: true);
        var indexed = Seed(notes, "n1", "Chili", "chili");
        var untouched = Seed(notes, "n2", "Kazan", "kazan");
        recall.Index(indexed, new NoteContent("fact", "Chili", "chili", null, null), "2026-08-17T00:00:00Z");

        var scores = recall.Score("chili", [indexed, untouched]);

        Assert.True(scores.ContainsKey(indexed));
        Assert.False(scores.ContainsKey(untouched));
    }

    [Fact]
    public void With_the_layer_off_nothing_is_indexed_or_scored()
    {
        using var temp = new TempDatabase();
        var (recall, store, notes) = NewRecall(temp, enabled: false);

        var id = Seed(notes, "n1", "Chili", "chili");
        recall.Index(id, new NoteContent("fact", "Chili", "chili", null, null), "2026-08-17T00:00:00Z");

        Assert.False(recall.Enabled);
        Assert.Null(recall.ModelId);
        Assert.Empty(store.ForScoring("test/marker-v1", MappingHashes(), [id]));
        Assert.Empty(recall.Score("chili", [id]));
    }

    /// <summary>
    /// Vectors from another model must never be scored: a cosine across two vector spaces is noise, not a weak
    /// signal. The same holds for another mapping — the text that produced the vector is no longer the text the
    /// note would produce today.
    /// </summary>
    [Fact]
    public void Passages_from_a_different_model_or_mapping_are_not_scored()
    {
        using var temp = new TempDatabase();
        var (recall, store, notes) = NewRecall(temp, enabled: true);
        var id = Seed(notes, "n1", "Chili", "chili");
        recall.Index(id, new NoteContent("fact", "Chili", "chili", null, null), "2026-08-17T00:00:00Z");

        Assert.Empty(store.ForScoring("some/other-model", MappingHashes(), [id]));
        Assert.Empty(store.ForScoring("test/marker-v1", ["a-different-mapping"], [id]));
        Assert.NotEmpty(store.ForScoring("test/marker-v1", MappingHashes(), [id]));
    }

    [Fact]
    public void The_index_build_work_list_names_notes_without_passages()
    {
        using var temp = new TempDatabase();
        var (recall, store, notes) = NewRecall(temp, enabled: true);
        var indexed = Seed(notes, "a", "Chili", "chili");
        var pending = Seed(notes, "b", "Kazan", "kazan");
        recall.Index(indexed, new NoteContent("fact", "Chili", "chili", null, null), "2026-08-17T00:00:00Z");

        var work = store.NeedingIndex("test/marker-v1", MappingHashes(), 10);

        Assert.Contains(pending, work);
        Assert.DoesNotContain(indexed, work);
    }

    /// <summary>
    /// The model is downloaded on demand, so at startup it usually is not there yet. Binding to it eagerly would
    /// leave semantic recall dead until someone restarted the server after the download — the hidden second step
    /// this whole feature exists to remove. The layer must come alive on <see cref="VectorRecall.Reload"/>.
    /// </summary>
    [Fact]
    public void The_layer_comes_alive_when_the_model_arrives_without_a_restart()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var notes = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        var downloaded = false;
        var loads = 0;
        var recall = new VectorRecall(
            () => { loads++; return downloaded ? new MarkerEmbedder() : null; },
            new PassageStore(factory), new LegacyRetrievalProjector(), new EmbeddingOptions(true, "unused-in-tests"));

        var id = Seed(notes, "n1", "Chili", "chili");
        Assert.False(recall.Enabled);
        Assert.Empty(recall.Score("chili", [id]));

        // A missing model must not mean re-attempting a load on every single search.
        Assert.Empty(recall.Score("chili", [id]));
        Assert.Equal(1, loads);

        downloaded = true;
        recall.Reload();
        recall.Index(id, new NoteContent("fact", "Chili", "chili", null, null), "2026-08-17T00:00:00Z");

        Assert.True(recall.Enabled);
        Assert.True(recall.Score("chili", [id])[id].Score > 0, "search should work as soon as the model lands");
    }

    private static IReadOnlyCollection<string> MappingHashes() => new LegacyRetrievalProjector().CurrentMappingHashes;

    // Passages are foreign-keyed to notes (so a purge cascades), which means a test must index a note that
    // actually exists rather than a bare id.
    private static (VectorRecall Recall, PassageStore Store, NotesRepository Notes) NewRecall(TempDatabase temp, bool enabled)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var store = new PassageStore(factory);
        var recall = new VectorRecall(new MarkerEmbedder(), store, new LegacyRetrievalProjector(),
            new EmbeddingOptions(enabled, "unused-in-tests"));
        return (recall, store, new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()));
    }

    private static string Seed(NotesRepository notes, string key, string title, string body) =>
        notes.Upsert("kitchen", "fact", title, body, """{ "statement": "seeded" }""", null, key, "tester").Id;
}
