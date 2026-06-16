namespace MemoryMcp.Core.Notes;

/// <summary>One outgoing link to create when assembling a note.</summary>
/// <param name="ToId">The target note id.</param>
/// <param name="Rel">The relation verb (active-voice lower_snake_case, e.g. <c>uses</c>, <c>depends_on</c>).</param>
public sealed record AssembleLink(string ToId, string Rel);

/// <summary>The result of an atomic assemble: the note plus how many links were created with it.</summary>
/// <param name="Id">The note id.</param>
/// <param name="Created">True if the note was newly created (false if a dedup-update).</param>
/// <param name="UpdatedUtc">The note's revision/etag after the write.</param>
/// <param name="LinksCreated">Number of links created in the same transaction.</param>
public sealed record AssembleResult(string Id, bool Created, string UpdatedUtc, int LinksCreated);
