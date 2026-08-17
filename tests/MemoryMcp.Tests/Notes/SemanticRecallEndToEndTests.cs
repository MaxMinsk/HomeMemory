using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Embeddings;
using MemoryMcp.Tests.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-196, end to end with the real model: the one thing lexical search cannot do at any price.
/// <para>The field case that started this work — an English query against a Russian note sharing not one token
/// — measured as a total miss on prod, and as position 1 under vectors. This asserts that gap closes, through
/// the whole stack: write, project, embed, store, search, fuse. Cyrillic is assembled from code points so the
/// source stays ASCII for the English gate.</para>
/// <para><b>Requires the model files</b>; reports itself skipped otherwise, which is right for CI, where the
/// layer is off and never loaded.</para>
/// </summary>
public class SemanticRecallEndToEndTests(ITestOutputHelper output)
{
    private static string Cyr(params int[] codePoints) => new(codePoints.Select(c => (char)c).ToArray());

    // "Shelly Pro 3EM - monitoring of the three-phase supply" — the note is Russian, the query will be English.
    private static readonly string ThreePhaseTitle =
        "Shelly Pro 3EM-3CT63 " + Cyr(0x043C, 0x043E, 0x043D, 0x0438, 0x0442, 0x043E, 0x0440, 0x0438, 0x043D, 0x0433) + " " +
        Cyr(0x0442, 0x0440, 0x0451, 0x0445, 0x0444, 0x0430, 0x0437, 0x043D, 0x043E, 0x0433, 0x043E) + " " +
        Cyr(0x0432, 0x0432, 0x043E, 0x0434, 0x0430);

    private const string Query = "three phase electricity monitoring device";

    [Fact]
    public void A_cross_language_query_finds_the_note_only_when_vectors_are_on()
    {
        if (ModelDirectory() is not { } directory)
        {
            output.WriteLine("SKIPPED: no model directory (set MEMORY_EMBEDDING_MODEL_DIR).");
            return;
        }

        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        using var embedder = new E5OnnxEmbedder(directory);
        // The projector the server actually registers, so this exercises the shipping path rather than a
        // configuration nothing runs (MEMP-252).
        var recall = new VectorRecall(embedder, new PassageStore(factory), new SchemaRetrievalProjector(registry),
            new EmbeddingOptions(Enabled: true, directory));

        // Lexical-only first: the same corpus, the same query, no vector layer.
        var lexical = new NotesRepository(factory, registry);
        Seed(lexical);
        var withoutVectors = lexical.Search(Query, domain: "home").Items.Select(hit => hit.Title).ToList();
        output.WriteLine("lexical: " + string.Join(" | ", withoutVectors));

        // Now index what is already there and search again through the same store.
        var semantic = new NotesRepository(factory, registry, vectors: recall);
        foreach (var note in semantic.Recent("home", null, 50, null, byUsage: false))
        {
            var full = semantic.Get(note.Id)!;
            recall.Index(full.Id, new NoteContent(full.Type, full.Title, full.Body, full.TagsJson, full.PayloadJson),
                "2026-08-17T00:00:00Z");
        }

        var page = semantic.Search(Query, domain: "home", explain: true);
        var withVectors = page.Items.Select(hit => hit.Title).ToList();
        output.WriteLine("hybrid : " + string.Join(" | ", withVectors));
        output.WriteLine($"relaxed: {page.Relaxed}");
        foreach (var hit in page.Items)
        {
            output.WriteLine(
                $"  lex {hit.Explain!.LexicalRank,2}  vec {hit.Explain.VectorScore,-8:F4} " +
                $"[{hit.Explain.VectorPassage}] fused {hit.Explain.Fused:F5}  {hit.Title}");
        }

        Assert.DoesNotContain(ThreePhaseTitle, withoutVectors);
        Assert.Equal(ThreePhaseTitle, withVectors[0]);

        // A semantic hit shares no words with the query, so "trust me, it is relevant" is all the caller would
        // otherwise get. It has to be able to see WHICH passage matched and which fields built it (MEMP-252).
        var winner = page.Items[0].Explain!;
        Assert.NotNull(winner.VectorPassage);
        Assert.NotEmpty(winner.VectorPaths!);

        // MEMP-265: the page contract must not contradict the page. `total` counts LEXICAL matches, and this
        // query has none — so a vector-only page used to report items alongside total=0, and hasMore=false told
        // a paginating caller to stop on a page it had not finished.
        Assert.True(page.Total >= page.Items.Count,
            $"total ({page.Total}) cannot be below the number of items returned ({page.Items.Count})");
        Assert.True(page.TotalIsLowerBound, "a semantic-candidate page must say its total is a floor, not a count");
    }

    // A small corpus with distractors, so ranking first actually means something.
    private static void Seed(NotesRepository notes)
    {
        // Every word of this note is Russian apart from the model number — which is the whole point, and why
        // its payload must not describe it in English either. An earlier version of this fixture said "three
        // phase meter" in the payload, and lexical search found the note through that, quietly proving nothing.
        var meter = Cyr(0x0421, 0x0447, 0x0451, 0x0442, 0x0447, 0x0438, 0x043A); // "meter"
        var onDinRail = " " + Cyr(0x043D, 0x0430) + " DIN-" + Cyr(0x0440, 0x0435, 0x0439, 0x043A, 0x0443) + ".";
        notes.Upsert("home", "fact", ThreePhaseTitle, meter + onDinRail,
            $$"""{ "statement": "{{meter}}" }""", null, "shelly", "tester");
        notes.Upsert("home", "fact", "Garden hose reel", "A wall mounted reel for the garden hose.",
            """{ "statement": "hose" }""", null, "hose", "tester");
        notes.Upsert("home", "fact", "Kitchen tap replacement", "The mixer tap in the kitchen drips.",
            """{ "statement": "tap" }""", null, "tap", "tester");
        notes.Upsert("home", "fact", "Firewood delivery", "Two cubic metres of birch, stacked by the shed.",
            """{ "statement": "firewood" }""", null, "firewood", "tester");
    }

    private static string? ModelDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("MEMORY_EMBEDDING_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && HasModel(configured))
        {
            return configured;
        }

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "huggingface", "hub", "models--intfloat--multilingual-e5-small", "snapshots");
        return Directory.Exists(cache) ? Directory.EnumerateDirectories(cache).FirstOrDefault(HasModel) : null;
    }

    private static bool HasModel(string directory) =>
        File.Exists(Path.Combine(directory, E5OnnxEmbedder.TokenizerFile));
}
