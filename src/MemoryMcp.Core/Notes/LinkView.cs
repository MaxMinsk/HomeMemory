namespace MemoryMcp.Core.Notes;

/// <summary>A note-link as seen from one note: its direction, relation, and the note at the other end.</summary>
/// <param name="Direction"><c>out</c> (this note → other) or <c>in</c> (other → this note).</param>
/// <param name="Rel">The relationship verb (e.g. <c>derived_from</c>, <c>uses</c>).</param>
/// <param name="NoteId">The note at the other end of the link.</param>
/// <param name="Title">That note's title, if any.</param>
/// <param name="Type">That note's type.</param>
/// <param name="Domain">That note's domain (for scope filtering / display).</param>
public sealed record LinkView(string Direction, string Rel, string NoteId, string? Title, string Type, string Domain);
