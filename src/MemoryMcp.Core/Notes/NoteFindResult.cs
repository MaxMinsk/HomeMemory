namespace MemoryMcp.Core.Notes;

/// <summary>One occurrence of a query inside a note's body, with a surrounding context window.</summary>
/// <param name="Offset">Character offset of the match in the body — pass to notes_read to read around it.</param>
/// <param name="Context">The matched text plus surrounding characters (a snippet, not the whole body).</param>
public sealed record NoteMatch(int Offset, string Context);

/// <summary>
/// The result of searching within a single note's body (ripgrep-on-a-file): matches with offsets and
/// context, so a caller can locate and read just the relevant parts of a large note.
/// </summary>
/// <param name="Id">The note id.</param>
/// <param name="Query">The query that was searched for.</param>
/// <param name="TotalChars">True length of the whole body in characters.</param>
/// <param name="MatchCount">Total number of matches found.</param>
/// <param name="Truncated">True when there are more matches than returned (raise the limit or page).</param>
/// <param name="Matches">The matches (capped at the requested limit), in document order.</param>
/// <param name="UpdatedUtc">The note's revision/etag when searched.</param>
public sealed record NoteFindResult(
    string Id, string Query, int TotalChars, int MatchCount, bool Truncated, IReadOnlyList<NoteMatch> Matches, string UpdatedUtc);
