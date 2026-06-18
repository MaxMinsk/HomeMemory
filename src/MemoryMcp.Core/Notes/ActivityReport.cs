namespace MemoryMcp.Core.Notes;

/// <summary>
/// A compact summary of recent write activity from the append-only event log (MEMP-186): total changes in the
/// window plus a breakdown by operation and by note type, scope-filtered. Body-free — counts only.
/// </summary>
/// <param name="Domain">The domain the report was scoped to, or null for all in scope.</param>
/// <param name="SinceUtc">The start of the window (ISO 8601); events at/after this are counted.</param>
/// <param name="Total">Total change events in the window.</param>
/// <param name="ByOp">Counts keyed by operation (create/update/supersede/archive/...).</param>
/// <param name="ByType">Counts keyed by note type.</param>
public sealed record ActivityReport(
    string? Domain, string SinceUtc, long Total,
    IReadOnlyDictionary<string, long> ByOp, IReadOnlyDictionary<string, long> ByType);
