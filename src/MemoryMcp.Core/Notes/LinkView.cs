namespace MemoryMcp.Core.Notes;

/// <summary>A note-link as seen from one note: its direction, relation, and the note at the other end.</summary>
/// <param name="Direction"><c>out</c> (this note → other) or <c>in</c> (other → this note).</param>
/// <param name="Rel">The relationship verb (e.g. <c>derived_from</c>, <c>uses</c>).</param>
/// <param name="NoteId">The note at the other end of the link.</param>
/// <param name="Title">That note's title, if any.</param>
/// <param name="Type">That note's type.</param>
/// <param name="Domain">That note's domain (for scope filtering / display).</param>
/// <param name="Status">That note's envelope lifecycle status (MEMP-241). Links traverse to retired notes on
/// purpose — "what did this replace?" is a fair question — so the caller has to be able to see that a neighbour
/// is <c>superseded</c> or <c>archived</c> rather than treating every link as current.</param>
public sealed record LinkView(string Direction, string Rel, string NoteId, string? Title, string Type, string Domain, string Status = "active");
