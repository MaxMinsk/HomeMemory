namespace MemoryMcp.Core.Notes;

/// <summary>The outcome of an upsert: the note id and whether it was newly created.</summary>
/// <param name="Id">The note's stable id.</param>
/// <param name="Created">True if a new note was created; false if an existing one was updated.</param>
public sealed record UpsertResult(string Id, bool Created);
