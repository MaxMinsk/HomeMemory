namespace MemoryMcp.Core.Notes;

/// <summary>
/// A windowed read of a note's body (so a caller reads only the part it needs of a large note).
/// Carries the partial-read contract: where the slice sits, how much came back, the true total, and a
/// continuation cursor — so the caller knows it has seen only part of the document and can read on.
/// </summary>
/// <param name="Id">The note id.</param>
/// <param name="Content">The slice of body text returned.</param>
/// <param name="Offset">The character offset this slice starts at (after clamping).</param>
/// <param name="ReturnedChars">Number of characters in <see cref="Content"/>.</param>
/// <param name="TotalChars">True length of the whole body in characters.</param>
/// <param name="Truncated">True when the slice ends before the end of the body.</param>
/// <param name="NextOffset">Where to continue reading (offset for the next call), or null at the end.</param>
/// <param name="UpdatedUtc">The note's revision/etag when read (detect edits between reads).</param>
public sealed record NoteReadSlice(
    string Id, string Content, int Offset, int ReturnedChars, int TotalChars, bool Truncated, int? NextOffset, string UpdatedUtc);
