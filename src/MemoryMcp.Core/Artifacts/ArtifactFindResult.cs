namespace MemoryMcp.Core.Artifacts;

/// <summary>One occurrence of a query inside an artifact's text, with surrounding context.</summary>
/// <param name="Offset">Character offset of the match.</param>
/// <param name="Context">The match plus surrounding characters.</param>
public sealed record ArtifactTextMatch(int Offset, string Context);

/// <summary>
/// Result of searching text inside an artifact (e.g. validating a rendered HTML/MD without downloading it):
/// matches with context, total/match counts, and the blob hash for provenance.
/// </summary>
/// <param name="Id">The artifact id.</param>
/// <param name="Sha256">The blob hash (provenance).</param>
/// <param name="Query">The query searched for.</param>
/// <param name="TotalChars">Length of the artifact's text in characters.</param>
/// <param name="MatchCount">Total matches found.</param>
/// <param name="Truncated">True when there are more matches than returned.</param>
/// <param name="Matches">The matches (capped at the requested limit).</param>
public sealed record ArtifactFindResult(
    string Id, string Sha256, string Query, int TotalChars, int MatchCount, bool Truncated, IReadOnlyList<ArtifactTextMatch> Matches);
