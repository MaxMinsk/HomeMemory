using System.Globalization;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// Whether text is being embedded to be STORED or to SEARCH (MEMP-196).
/// <para>Not cosmetic: retrieval models are trained asymmetrically and expect the two sides to be marked
/// differently (e5 prefixes them <c>passage:</c> and <c>query:</c>). Getting it wrong costs quality silently —
/// nothing fails, results are simply worse — so the distinction is in the type system rather than left to a
/// caller to remember.</para>
/// </summary>
public enum EmbeddingKind
{
    /// <summary>Text being indexed.</summary>
    Passage,

    /// <summary>Text being searched for.</summary>
    Query,
}

/// <summary>
/// Turns text into vectors (MEMP-196). Implementations live outside Core so that the embedding runtime and its
/// native binaries are a deployment choice rather than a dependency of the domain — a server with the layer
/// switched off loads none of it.
/// </summary>
public interface IEmbedder
{
    /// <summary>
    /// Identity of the model, stored with every vector. A change makes existing vectors stale rather than
    /// silently comparable: two models' spaces are not interchangeable, and a cosine between them is noise.
    /// </summary>
    string ModelId { get; }

    /// <summary>Vector length this model produces.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Embeds a batch, returning L2-normalised vectors in the same order — normalised so a dot product IS the
    /// cosine, and no ranking code has to remember to divide.
    /// </summary>
    /// <param name="texts">Texts to embed.</param>
    /// <param name="kind">Whether these are passages being indexed or a query being searched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for the opt-in embedding layer (MEMP-196).
/// </summary>
/// <param name="Enabled">
/// Master switch, <b>off by default</b>. With it off nothing is loaded, nothing is embedded and retrieval
/// behaves exactly as it did before the layer existed — FTS5 remains the always-on baseline, which is what
/// ADR 0003 promised and what this amends rather than overturns.
/// </param>
/// <param name="ModelDirectory">
/// Directory holding the model files. Deliberately NOT baked into the add-on image: the runtime costs ~25 MB
/// per architecture, but the model is far larger, and a user who never enables the layer should not carry it.
/// </param>
/// <param name="Weight">
/// How much the vector signal counts in the hybrid blend, relative to the other signals. Query-time, so it can
/// be retuned without reindexing anything.
/// </param>
public sealed record EmbeddingOptions(bool Enabled = false, string? ModelDirectory = null, double Weight = 2.0)
{
    /// <summary>Reads <c>MEMORY_EMBEDDINGS</c>, <c>MEMORY_EMBEDDING_MODEL_DIR</c> and <c>MEMORY_EMBEDDING_WEIGHT</c>.</summary>
    public static EmbeddingOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("MEMORY_EMBEDDINGS");
        var enabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1";
        var weight = double.TryParse(
            Environment.GetEnvironmentVariable("MEMORY_EMBEDDING_WEIGHT"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 2.0;
        return new EmbeddingOptions(enabled, Environment.GetEnvironmentVariable("MEMORY_EMBEDDING_MODEL_DIR"), weight);
    }
}
