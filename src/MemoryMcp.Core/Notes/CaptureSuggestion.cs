namespace MemoryMcp.Core.Notes;

/// <summary>An existing note that capture-help surfaced, with why it is relevant to the candidate.</summary>
/// <param name="Id">The existing note id.</param>
/// <param name="Title">Its title, if any.</param>
/// <param name="Type">Its type.</param>
/// <param name="Domain">Its domain.</param>
/// <param name="Why">Why it matched: identical content / same title &amp; type / text match.</param>
public sealed record CaptureCandidate(string Id, string? Title, string Type, string Domain, string Why);

/// <summary>
/// Advice on whether to capture a candidate note (MEMP-111): an <see cref="Action"/> of
/// <c>save</c> / <c>update</c> / <c>skip</c> / <c>ask</c>, the reason, and any existing notes that informed it.
/// Read-only and rule-based — it writes nothing and never decides for the caller; it just flags likely duplicates.
/// </summary>
/// <param name="Action">save (no match) / update (same title+type) / skip (identical or empty) / ask (similar found).</param>
/// <param name="Reason">Human-readable explanation of the action.</param>
/// <param name="Candidates">Existing notes behind the action (empty for save/skip-empty).</param>
public sealed record CaptureSuggestion(string Action, string Reason, IReadOnlyList<CaptureCandidate> Candidates);
