namespace MemoryMcp.Core.Notes;

/// <summary>
/// One page of search hits plus the totals a caller needs to paginate — so an agent fetches a
/// bounded page and follows up with <c>offset</c>, instead of pulling the whole store at once.
/// </summary>
/// <param name="Items">Hits on this page.</param>
/// <param name="Total">Total matches across all pages.</param>
/// <param name="Offset">Offset of this page (0-based).</param>
/// <param name="Limit">Page size actually used (after clamping).</param>
/// <param name="HasMore">True when more results exist beyond this page.</param>
/// <param name="Relaxed">True when an AND query found nothing and was auto-widened to an any-term (OR) ranked search (MEMP-190).</param>
public sealed record SearchPage(
    IReadOnlyList<SearchResult> Items, int Total, int Offset, int Limit, bool HasMore, bool Relaxed = false);
