namespace MemoryMcp.Core.Notes;

/// <summary>
/// A compact entry from a note's append-only history (<c>note_events</c>): enough to scan the timeline
/// without paying for the full before/after snapshot. Fetch the heavy detail on demand by
/// <see cref="EventId"/> (see <see cref="NoteEventDetail"/>).
/// </summary>
/// <param name="EventId">Stable event id (pass to fetch the full detail).</param>
/// <param name="Op">The operation (create / update / patch / archive / restore / link / unlink / supersede).</param>
/// <param name="Actor">Who performed it (provenance), if recorded.</param>
/// <param name="Ts">When it happened (ISO-8601 UTC).</param>
/// <param name="ChangedFields">Which mutable fields changed (title/body/payload/tags/status), for update/patch/create.</param>
/// <param name="DiffBytes">Size of the full diff in characters — how heavy the detail is to fetch.</param>
public sealed record NoteEvent(string EventId, string Op, string? Actor, string Ts, IReadOnlyList<string> ChangedFields, int DiffBytes);

/// <summary>The detail of one history event, fetched on demand — optionally projected to chosen fields
/// and/or capped, so a huge diff doesn't flood the caller's context in one call.</summary>
/// <param name="EventId">Stable event id.</param>
/// <param name="Op">The operation.</param>
/// <param name="Actor">Who performed it (provenance), if recorded.</param>
/// <param name="Ts">When it happened (ISO-8601 UTC).</param>
/// <param name="DiffJson">The diff JSON for the requested projection, truncated to maxChars if asked.</param>
/// <param name="DiffChars">Full length of the requested projection before any truncation.</param>
/// <param name="Truncated">True when DiffJson was cut to maxChars.</param>
public sealed record NoteEventDetail(string EventId, string Op, string? Actor, string Ts, string? DiffJson, int DiffChars, bool Truncated);
