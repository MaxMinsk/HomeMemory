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
/// <param name="Project">The note's project sub-axis (echoed so callers can confirm the scope was set/preserved).</param>
/// <param name="Related">Up to a few notes related to a NEWLY created note (a linking hint, MEMP-205); null on updates or when disabled.</param>
/// <param name="Nudge">An advisory hint when an identified agent wrote without a prior recall this session (MEMP-204); null otherwise.</param>
public sealed record AssembleResult(string Id, bool Created, string UpdatedUtc, int LinksCreated, string? Project = null,
    IReadOnlyList<RelatedNote>? Related = null, string? Nudge = null);

/// <summary>One link to create in a bulk assemble (MEMP-218). Endpoints are addressed by a batch item's
/// dedupKey or by an existing note id, so notes created in the same call can be linked immediately.</summary>
/// <param name="From">Source: a batch item's dedupKey, or an existing note id.</param>
/// <param name="To">Target: a batch item's dedupKey, or an existing note id.</param>
/// <param name="Rel">The relation verb (active-voice lower_snake_case).</param>
public sealed record AssembleManyLink(string From, string To, string Rel);

/// <summary>The result of an atomic bulk assemble (MEMP-218): per-item upsert results plus link tallies.</summary>
/// <param name="Items">One result per upserted item, in order (id, created/unchanged, revision, type, dedupKey).</param>
/// <param name="LinksCreated">Links newly created in the same transaction.</param>
/// <param name="LinksAlreadyPresent">Links that already existed (idempotent no-ops).</param>
public sealed record AssembleManyResult(IReadOnlyList<UpsertResult> Items, int LinksCreated, int LinksAlreadyPresent);
