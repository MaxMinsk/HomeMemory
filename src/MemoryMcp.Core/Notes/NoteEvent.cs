namespace MemoryMcp.Core.Notes;

/// <summary>An entry from a note's append-only history (<c>note_events</c>).</summary>
/// <param name="Op">The operation (create / update / archive / restore / link / unlink / supersede).</param>
/// <param name="Actor">Who performed it (provenance), if recorded.</param>
/// <param name="Ts">When it happened (ISO-8601 UTC).</param>
/// <param name="DiffJson">A small JSON diff/details payload, if any.</param>
public sealed record NoteEvent(string Op, string? Actor, string Ts, string? DiffJson);
