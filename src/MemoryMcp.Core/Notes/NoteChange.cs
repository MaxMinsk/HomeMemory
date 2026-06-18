namespace MemoryMcp.Core.Notes;

/// <summary>One change to a note from the append-only event log (a changefeed row).</summary>
/// <param name="EventId">The event id (part of the cursor).</param>
/// <param name="Op">create / update / supersede / archive / restore / etc.</param>
/// <param name="Ts">Event timestamp (ISO-8601 UTC; part of the cursor).</param>
/// <param name="NoteId">The note that changed.</param>
/// <param name="Title">The note's current title, if any.</param>
/// <param name="Type">The note's type.</param>
/// <param name="Domain">The note's domain.</param>
/// <param name="Project">The note's project axis, if any.</param>
/// <param name="Status">The note's current envelope status.</param>
public sealed record NoteChange(
    string EventId, string Op, string Ts, string NoteId, string? Title, string Type, string Domain, string? Project, string Status);

/// <summary>
/// A page of the changefeed (MEMP-155): note changes after a cursor, oldest-first, for polling-based
/// "subscriptions". The consumer stores <see cref="NextCursor"/> and passes it as the next <c>since</c>.
/// </summary>
/// <param name="Changes">The changes in this page, oldest-first.</param>
/// <param name="NextCursor">Opaque cursor to resume after the last change (null when none returned).</param>
/// <param name="HasMore">True when more changes exist beyond this page (poll again immediately).</param>
public sealed record NoteChangePage(IReadOnlyList<NoteChange> Changes, string? NextCursor, bool HasMore);
