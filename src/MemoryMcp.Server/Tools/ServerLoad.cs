using MemoryMcp.Core.Diagnostics;

namespace MemoryMcp.Server.Tools;

/// <summary>
/// What the server costs to run (MEMP-247): the runtime figures every build has, plus the embedding layer's
/// own cost and coverage when it is switched on.
/// <para>Composed rather than merged, because the two have different lifetimes — the runtime window resets on
/// restart while index coverage is durable — and a caller reading one should not have to guess which it got.</para>
/// </summary>
/// <param name="Runtime">Per-operation timings and process figures.</param>
/// <param name="Embeddings">Vector layer state and index coverage.</param>
public sealed record ServerLoad(LoadReport Runtime, EmbeddingLoad Embeddings);
