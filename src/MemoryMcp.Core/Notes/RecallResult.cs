namespace MemoryMcp.Core.Notes;

/// <summary>A note pulled into a recall through the link graph (a neighbor of a primary hit).</summary>
/// <param name="Id">The neighbor note id.</param>
/// <param name="Title">Its title, if any.</param>
/// <param name="Type">Its type.</param>
/// <param name="Domain">Its domain.</param>
/// <param name="Rel">The relation connecting it to the primary hit.</param>
/// <param name="Direction"><c>out</c> (hit → neighbor) or <c>in</c> (neighbor → hit).</param>
/// <param name="FromId">The primary hit this neighbor was reached from.</param>
public sealed record RecallNeighbor(string Id, string? Title, string Type, string Domain, string Rel, string Direction, string FromId);

/// <summary>
/// A compact, prompt-ready context block for a query: the primary search hits plus the notes they link to
/// (one hop, both directions), so an agent gets a case's surrounding context in a single call. Snippets
/// only — never full bodies; pull a body with notes_read/notes_get when needed.
/// </summary>
/// <param name="Query">The query that produced the recall (may be null for structured-only recall).</param>
/// <param name="Hits">The primary matches (with snippet/payload), scope-restricted.</param>
/// <param name="Neighbors">Linked neighbor notes of the hits (deduped, scope-restricted).</param>
/// <param name="BudgetChars">The snippet-char budget requested, or null when paging by row count (MEMP-176).</param>
/// <param name="UsedChars">Sum of the included hits' snippet chars, when budgeted (else null).</param>
/// <param name="DroppedCount">How many ranked hits the budget left out, when budgeted (else null).</param>
public sealed record RecallResult(
    string? Query, IReadOnlyList<SearchResult> Hits, IReadOnlyList<RecallNeighbor> Neighbors,
    int? BudgetChars = null, int? UsedChars = null, int? DroppedCount = null);
