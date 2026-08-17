using MemoryMcp.Core.Diagnostics;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// The vector half of hybrid recall (MEMP-196): indexes a note's passages on write, and scores candidate notes
/// against a query on read.
/// <para>It exists so the reader and writer need to know only "is there a vector signal for these notes?"
/// rather than anything about models, prefixes or storage — and so that with the layer switched off there is
/// exactly one cheap boolean between them and the old behaviour.</para>
/// <para><b>Scoring is best-passage, not average.</b> A note is relevant if ANY part of it is about the query;
/// averaging its passages would dilute a precise match into the note's general topic, which is the same
/// mistake as embedding the whole note as one vector — measured to be worse than indexing the title alone.</para>
/// </summary>
public sealed class VectorRecall
{
    private readonly Func<IEmbedder?> _resolve;
    private readonly PassageStore? _passages;
    private readonly IRetrievalProjector _projector;
    private readonly EmbeddingOptions _options;
    private readonly OperationMetrics? _metrics;
    private IEmbedder? _embedder;
    private bool _resolved;

    /// <summary>Creates the layer over an already-constructed embedder (or none).</summary>
    /// <param name="embedder">The model, or null to leave the layer off.</param>
    /// <param name="passages">The embedding index.</param>
    /// <param name="projector">Where passages come from.</param>
    /// <param name="options">Embedding configuration.</param>
    /// <param name="metrics">Optional load telemetry.</param>
    public VectorRecall(IEmbedder? embedder, PassageStore? passages, IRetrievalProjector projector,
        EmbeddingOptions options, OperationMetrics? metrics = null)
        : this(() => embedder, passages, projector, options, metrics)
    {
    }

    /// <summary>
    /// Creates the layer over a factory that loads the model on first use.
    /// <para>Lazy on purpose: the model is downloaded on demand, so at startup it may not exist yet. Binding
    /// eagerly would mean the layer stayed dead until someone restarted the server after the download — which
    /// is exactly the kind of hidden second step this feature is trying to avoid.</para>
    /// </summary>
    /// <param name="embedderFactory">Loads the model, or returns null when it is not available yet.</param>
    /// <param name="passages">The embedding index.</param>
    /// <param name="projector">Where passages come from.</param>
    /// <param name="options">Embedding configuration.</param>
    /// <param name="metrics">Optional load telemetry.</param>
    public VectorRecall(Func<IEmbedder?> embedderFactory, PassageStore? passages, IRetrievalProjector projector,
        EmbeddingOptions options, OperationMetrics? metrics = null)
    {
        _resolve = embedderFactory ?? throw new ArgumentNullException(nameof(embedderFactory));
        _passages = passages;
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics;
    }

    // Resolved once, success or failure, so a missing model does not mean re-attempting a load on every
    // search. Reload() is the deliberate way to try again after the model has been fetched.
    private IEmbedder? Embedder
    {
        get
        {
            if (_resolved)
            {
                return _embedder;
            }

            _embedder = _resolve();
            _resolved = true;
            return _embedder;
        }
    }

    /// <summary>Forgets a failed load so the next call tries again — called once the model has been fetched.</summary>
    public void Reload() => _resolved = false;

    // Timed because "is this too heavy for the box?" must be a reading, not an argument (MEMP-247). Query and
    // index embedding are recorded separately: one happens on every search, the other once per write, and they
    // fail in different ways.
    private IReadOnlyList<float[]> Timed(string operation, IReadOnlyList<string> texts, EmbeddingKind kind) =>
        _metrics is null
            ? Embedder!.Embed(texts, kind)
            : _metrics.Measure(operation, () => Embedder!.Embed(texts, kind));

    /// <summary>True when a model is loaded and the layer is switched on; false makes every method a no-op.</summary>
    public bool Enabled => _options.Enabled && _passages is not null && Embedder is not null;

    /// <summary>The model producing vectors, or null when the layer is off.</summary>
    public string? ModelId => Enabled ? Embedder!.ModelId : null;

    /// <summary>Weight of the vector signal in the hybrid blend (query-time, so retuning needs no reindex).</summary>
    public double Weight => _options.Weight;

    /// <summary>Rebuilds one note's passages. A no-op when the layer is off.</summary>
    /// <param name="noteId">The note.</param>
    /// <param name="content">Its content, for the projector.</param>
    /// <param name="nowUtc">Timestamp to record.</param>
    public void Index(string noteId, NoteContent content, string nowUtc)
    {
        if (!Enabled)
        {
            return;
        }

        var projected = _projector.Passages(content);
        if (projected.Count == 0)
        {
            _passages!.Delete(noteId);
            return;
        }

        var vectors = Timed("embed_passages", [.. projected.Select(passage => passage.Text)], EmbeddingKind.Passage);
        _passages!.Replace(noteId, projected, vectors, Embedder!, _projector.Describe(content.Type).MappingHash, nowUtc);
    }

    /// <summary>Drops a note's passages (used when it is purged).</summary>
    /// <param name="noteId">The note.</param>
    public void Forget(string noteId)
    {
        if (Enabled)
        {
            _passages!.Delete(noteId);
        }
    }

    /// <summary>
    /// Cosine of each candidate note's best passage against the query, for the notes that have one. Notes
    /// missing from the result simply have no vector evidence — they are NOT scored zero, because "not indexed
    /// yet" and "indexed and unrelated" are different claims and only the ranker may decide what to do with either.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="noteIds">The candidate pool to score.</param>
    public IReadOnlyDictionary<string, double> Score(string? query, IReadOnlyCollection<string> noteIds)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        if (!Enabled || string.IsNullOrWhiteSpace(query) || noteIds.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var queryVector = Timed("embed_query", [query!], EmbeddingKind.Query)[0];
        var mappingHash = _projector.Describe(string.Empty).MappingHash;
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var passage in _passages!.ForScoring(Embedder!.ModelId, mappingHash, noteIds))
        {
            var score = Dot(passage.Vector, queryVector);
            if (!best.TryGetValue(passage.NoteId, out var current) || score > current)
            {
                best[passage.NoteId] = score;
            }
        }

        return best;
    }

    /// <summary>
    /// The most semantically similar notes in the whole index, best first — semantic CANDIDATES, not a re-rank.
    /// <para>This is the difference between a vector layer that works and one that only looks like it does. If
    /// vectors could merely re-order what BM25 already found, they could never answer the case the feature
    /// exists for: an English query against a Russian note sharing not one token, which BM25 never surfaces at
    /// all. Candidates must be able to ENTER the pool.</para>
    /// <para>A brute-force scan is deliberate at this corpus size — a few thousand passages of 384 floats is a
    /// handful of milliseconds, and an approximate index would add a dependency, a build step and a staleness
    /// problem to save time nobody can feel. Revisit when the corpus is an order of magnitude larger.</para>
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="limit">How many candidate notes to return.</param>
    public IReadOnlyList<string> Candidates(string? query, int limit)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<string>();
        }

        var queryVector = Timed("embed_query", [query!], EmbeddingKind.Query)[0];
        var mappingHash = _projector.Describe(string.Empty).MappingHash;
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var passage in _passages!.ForScoring(Embedder!.ModelId, mappingHash))
        {
            var score = Dot(passage.Vector, queryVector);
            if (!best.TryGetValue(passage.NoteId, out var current) || score > current)
            {
                best[passage.NoteId] = score;
            }
        }

        return [.. best.OrderByDescending(pair => pair.Value).Take(limit).Select(pair => pair.Key)];
    }

    // Both sides are L2-normalised by the embedder, so the dot product IS the cosine. A length mismatch means
    // two different models' vectors reached the same query, which must never be scored rather than fudged.
    private static double Dot(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            return 0d;
        }

        var sum = 0d;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * (double)right[i];
        }

        return sum;
    }
}
