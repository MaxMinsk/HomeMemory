namespace MemoryMcp.Core.Diagnostics;

/// <summary>
/// How far the embedding index has got, and at what cost (MEMP-247).
/// <para>Reported so "is the embedder too heavy for this box" is a reading rather than an argument, and so a
/// half-built index is visible as such: <see cref="IndexedNotes"/> below <see cref="ActiveNotes"/> means
/// semantic recall is answering from part of the corpus, which is worth knowing before trusting a result.</para>
/// </summary>
/// <param name="Enabled">True when a model is loaded and the layer is live.</param>
/// <param name="Model">Model identity, or null when the layer is off.</param>
/// <param name="IndexedNotes">Notes carrying passages under a current model and mapping.</param>
/// <param name="ActiveNotes">Active notes in the corpus — the denominator for build progress.</param>
/// <param name="StalePassages">Passages from an older model or mapping; they are not scored, only rebuilt.</param>
public sealed record EmbeddingLoad(bool Enabled, string? Model, long IndexedNotes, long ActiveNotes, long StalePassages);
