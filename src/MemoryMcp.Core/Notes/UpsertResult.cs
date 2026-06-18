namespace MemoryMcp.Core.Notes;

/// <summary>The outcome of an upsert: the note id, whether it was newly created, and its new revision.</summary>
/// <param name="Id">The note's stable id.</param>
/// <param name="Created">True if a new note was created; false if an existing one was updated.</param>
/// <param name="UpdatedUtc">The note's new <c>updated_utc</c> — use as the revision/etag for a later patch.</param>
/// <param name="Type">The note's type (echoed so a write needn't be followed by a get).</param>
/// <param name="DedupKey">The note's stable dedup key, if any (echoed for the same reason).</param>
/// <param name="Unchanged">True when an upsert was a no-op because the content was identical (MEMP-183): no revision bump, no event.</param>
public sealed record UpsertResult(string Id, bool Created, string? UpdatedUtc = null, string? Type = null, string? DedupKey = null, bool Unchanged = false);
