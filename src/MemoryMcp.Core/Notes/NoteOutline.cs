namespace MemoryMcp.Core.Notes;

/// <summary>A Markdown heading found in a note's body, with the character offset of its line start.</summary>
/// <param name="Level">Heading level (1–6, i.e. the number of leading <c>#</c>).</param>
/// <param name="Text">The heading text (without the <c>#</c> markers).</param>
/// <param name="Offset">Character offset of the heading line in the body — pass to notes_read to read the section.</param>
public sealed record NoteHeading(int Level, string Text, int Offset);

/// <summary>
/// The Markdown heading map of a note's body, so a caller can jump to a section (read from a heading's
/// offset up to the next heading's offset) instead of pulling the whole body.
/// </summary>
/// <param name="Id">The note id.</param>
/// <param name="TotalChars">True length of the whole body in characters.</param>
/// <param name="UpdatedUtc">The note's revision/etag when read.</param>
/// <param name="Headings">The headings in document order.</param>
public sealed record NoteOutline(string Id, int TotalChars, string UpdatedUtc, IReadOnlyList<NoteHeading> Headings);
