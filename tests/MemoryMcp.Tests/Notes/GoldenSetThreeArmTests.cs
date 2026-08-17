using System.Globalization;
using System.Text.Json;
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
/// MEMP-252's deciding measurement: three arms over one corpus and one golden set — lexical only, legacy
/// all-strings vectorisation, and schema-selected passages.
/// <para><b>Why this is a test and not a script.</b> A Python harness would have to re-implement the projectors,
/// and would then be measuring the re-implementation. This drives the REAL <see cref="LegacyRetrievalProjector"/>
/// and <see cref="SchemaRetrievalProjector"/> through the real writer, indexer and ranker, so a result here is a
/// statement about what ships. It also survives as a regression guard: MEMP-262's lane migration has to be
/// measured against exactly this table.</para>
/// <para>Skips unless BOTH the model and the exported corpus are present, which is right for CI — neither is in
/// the repository, and neither belongs there.</para>
/// </summary>
public class GoldenSetThreeArmTests(ITestOutputHelper output)
{
    private sealed record CorpusNote(string Id, string Title, string Type, string Domain, string? Body, string? TagsJson, string? PayloadJson);

    // The golden set is DATA, and it lives with the corpus it is measured against, in the gitignored
    // Notes~/embedding-eval/golden.json. Two reasons, and only the first is about this repository being
    // English-only: these are real queries a person typed against real Russian notes, so half of them cannot be
    // written here at all. The better reason is that queries and corpus have to move together — a golden set
    // pinned in source while the corpus is re-exported measures a corpus that no longer exists.
    private static readonly (string Query, string Domain, string Expected)[] Golden = LoadGolden();

    [Fact]
    public void Schema_selected_passages_are_measured_against_legacy_extraction_and_lexical_only()
    {
        if (ModelDirectory() is not { } directory || Golden.Length == 0 || LoadCorpus() is not { Count: > 0 } corpus)
        {
            output.WriteLine("SKIPPED: needs MEMORY_EMBEDDING_MODEL_DIR plus Notes~/embedding-eval/{golden.json,corpus_payloads.jsonl}.");
            return;
        }

        output.WriteLine($"corpus: {corpus.Count} notes, {corpus.Count(note => !string.IsNullOrWhiteSpace(note.PayloadJson))} with a payload, "
            + $"{corpus.Count(note => string.IsNullOrWhiteSpace(note.Body))} with an empty body");

        using var embedder = new E5OnnxEmbedder(directory);
        var lexical = Measure(corpus, null, directory, embedder, out _);
        var legacy = Measure(corpus, _ => new LegacyRetrievalProjector(), directory, embedder, out var legacyPassages);
        var schema = Measure(corpus, registry => new SchemaRetrievalProjector(registry), directory, embedder, out var schemaPassages);

        output.WriteLine("");
        output.WriteLine("| # | query | lexical | legacy vectors | schema vectors |");
        output.WriteLine("| - | ----- | ------- | -------------- | -------------- |");
        for (var i = 0; i < Golden.Length; i++)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {i + 1} | {Golden[i].Query} | {Show(lexical[i])} | {Show(legacy[i])} | {Show(schema[i])} |"));
        }

        output.WriteLine("");
        output.WriteLine($"recall@10 : lexical {Recall(lexical)}/{Golden.Length}, legacy {Recall(legacy)}/{Golden.Length}, schema {Recall(schema)}/{Golden.Length}");
        output.WriteLine($"mean rank : lexical {MeanRank(lexical):F1}, legacy {MeanRank(legacy):F1}, schema {MeanRank(schema):F1}   (misses counted as 200)");
        output.WriteLine($"passages  : legacy {legacyPassages}, schema {schemaPassages} "
            + $"({100.0 * (legacyPassages - schemaPassages) / Math.Max(1, legacyPassages):F0}% fewer vectors to store and scan)");

        // The bar MEMP-252 set: selecting fields must not be WORSE than sweeping every string in. It is
        // deliberately not "must be better" — the honest outcome may be that the two tie on this corpus and the
        // win is explainability and index size rather than recall, and that is worth recording either way.
        Assert.True(Recall(schema) >= Recall(legacy),
            $"schema-selected recall@10 ({Recall(schema)}) must be at least legacy ({Recall(legacy)}); see the table above");
    }

    // Builds a fresh database, seeds the corpus, indexes it under the given projector (null = no vector layer at
    // all) and returns each golden query's 1-based position, or null for a miss.
    private static int?[] Measure(
        IReadOnlyList<CorpusNote> corpus, Func<SchemaRegistry, IRetrievalProjector>? projectorFor,
        string directory, IEmbedder embedder, out long passageCount)
    {
        passageCount = 0;
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var registry = SchemaRegistry.FromEmbeddedResources();
        RegisterExportedSchemas(registry, factory);
        var notes = new NotesRepository(factory, registry);

        var seeded = Seed(corpus, notes);

        VectorRecall? vectors = null;
        if (projectorFor is not null)
        {
            var projector = projectorFor(registry);
            var passages = new PassageStore(factory);
            vectors = new VectorRecall(embedder, passages, projector, new EmbeddingOptions(Enabled: true, directory));
            foreach (var (id, note) in seeded)
            {
                vectors.Index(id, new NoteContent(note.Type, note.Title, note.Body, note.TagsJson, note.PayloadJson),
                    "2026-08-17T00:00:00Z");
            }

            passageCount = passages.Coverage(embedder.ModelId, projector.CurrentMappingHashes).Current;

            // A near-tie between the arms would be indistinguishable from the schema arm having silently fallen
            // back to legacy for every type — which is exactly what happens if the annotations do not reach the
            // schemas the corpus actually uses. So state plainly which types were mapped.
            if (projector is SchemaRetrievalProjector schemaProjector)
            {
                var (mapped, onLegacy) = schemaProjector.MappingCoverage();
                Console.WriteLine($"MAPPED TYPES: {mapped} annotated, {onLegacy} on legacy. "
                    + $"recipe -> {(schemaProjector.Describe("recipe").IsLegacy ? "LEGACY" : schemaProjector.Describe("recipe").MappingVersion)}, "
                    + $"backlog_item -> {(schemaProjector.Describe("backlog_item").IsLegacy ? "LEGACY" : "mapped")}, "
                    + $"reference -> {(schemaProjector.Describe("reference").IsLegacy ? "LEGACY" : "mapped")}");
            }
        }

        var reader = new NotesRepository(factory, registry, vectors: vectors);
        var positions = new int?[Golden.Length];
        for (var i = 0; i < Golden.Length; i++)
        {
            var (query, domain, expected) = Golden[i];
            var hits = reader.Search(query, domain: domain, limit: 50).Items;
            var at = hits.ToList().FindIndex(hit =>
                hit.Title?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true);
            positions[i] = at < 0 ? null : at + 1;
        }

        return positions;
    }

    // Registers the schemas exported from the live server, plus any hand-annotated overrides. Without this the
    // corpus is validated against the schemas this BUILD ships, which are behind what the server actually runs —
    // and every note of a newer type would be seeded with its payload stripped, quietly gutting the measurement.
    private static void RegisterExportedSchemas(SchemaRegistry registry, ISqliteConnectionFactory factory)
    {
        var root = Path.Combine(RepositoryRoot(), "Notes~", "embedding-eval");
        foreach (var folder in new[] { "schemas", "schemas-annotated" })
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    registry.Upsert(factory, File.ReadAllText(file), "eval");
                }
                catch (SchemaAuthoringException)
                {
                    // A built-in version is read-only, which is correct: this build's annotated copy must win
                    // over the unannotated one the server currently has.
                }
            }
        }
    }

    // Seeds the corpus into a fresh database. A payload that no longer validates is seeded WITHOUT it rather
    // than skipped: dropping the note would change the corpus between arms, and a comparison across different
    // corpora measures nothing.
    private static List<(string Id, CorpusNote Note)> Seed(IReadOnlyList<CorpusNote> corpus, NotesRepository notes)
    {
        var seeded = new List<(string Id, CorpusNote Note)>();
        var withoutPayload = 0;
        foreach (var note in corpus)
        {
            string? id = null;
            foreach (var payload in new[] { note.PayloadJson, null })
            {
                try
                {
                    id = notes.Upsert(note.Domain, note.Type, note.Title, note.Body, payload, note.TagsJson, note.Id, "eval").Id;
                    break;
                }
                catch (Exception exception) when (exception is NoteValidationException or InvalidOperationException or ArgumentException or JsonException)
                {
                    if (payload is not null)
                    {
                        withoutPayload++;
                    }
                }
            }

            if (id is not null)
            {
                seeded.Add((id, note));
            }
        }

        // Surfaced rather than swallowed: if many payloads failed to validate, the arms are comparing a corpus
        // that has had its payloads stripped — precisely the content this measurement is about.
        if (withoutPayload > 0)
        {
            Console.WriteLine($"WARNING: {withoutPayload} note(s) seeded without their payload (schema mismatch).");
        }

        return seeded;
    }

    private static string Show(int? position) =>
        position is null ? "MISS" : position.Value.ToString(CultureInfo.InvariantCulture);

    private static int Recall(int?[] positions) => positions.Count(position => position is > 0 and <= 10);

    private static double MeanRank(int?[] positions) => positions.Average(position => (double)(position ?? 200));

    // Joins the two exports: bodies were paged out for MEMP-242, payloads and tags for this ticket. Kept apart
    // because bodies are expensive to fetch (one call per note) and payloads are cheap (a hundred per page), so
    // re-exporting one should never mean re-exporting the other.
    private static IReadOnlyList<CorpusNote>? LoadCorpus()
    {
        var directory = Path.Combine(RepositoryRoot(), "Notes~", "embedding-eval");
        var payloadsPath = Path.Combine(directory, "corpus_payloads.jsonl");
        if (!File.Exists(payloadsPath))
        {
            return null;
        }

        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "corpus_bodies_*.jsonl"))
        {
            foreach (var row in Rows(file))
            {
                if (Text(row, "id") is { } id && Text(row, "body") is { } body)
                {
                    bodies[id] = body;
                }
            }
        }

        var notes = new List<CorpusNote>();
        foreach (var row in Rows(payloadsPath))
        {
            var id = Text(row, "id");
            if (id is null)
            {
                continue;
            }

            notes.Add(new CorpusNote(
                id, Text(row, "title") ?? string.Empty, Text(row, "type") ?? "fact", Text(row, "domain") ?? "kitchen",
                bodies.GetValueOrDefault(id), Text(row, "tagsJson"), Text(row, "payloadJson")));
        }

        return notes;
    }

    // Expected-title fragments must be UNIQUE in the corpus: a short one such as "Chili" also matches a
    // measurement note and a pepper reference, and the measurement would then score whichever ranked first
    // rather than the note it means.
    private static (string Query, string Domain, string Expected)[] LoadGolden()
    {
        var path = Path.Combine(RepositoryRoot(), "Notes~", "embedding-eval", "golden.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(row =>
            (Text(row, "query") ?? string.Empty, Text(row, "domain") ?? "kitchen", Text(row, "expected") ?? string.Empty))];
    }

    private static IEnumerable<JsonElement> Rows(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement row;
            try
            {
                row = JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            yield return row;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static string? ModelDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("MEMORY_EMBEDDING_MODEL_DIR");
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ? null : directory;
    }
}
