namespace MemoryMcp.Core.Backlog;

/// <summary>A backlog entry parsed from (or rendered to) a markdown backlog file.</summary>
/// <param name="Key">The MEMP-style key (also the dedup key).</param>
/// <param name="Status">Lifecycle status: ready/next/later/in_progress/done/blocked.</param>
/// <param name="Title">Short title.</param>
/// <param name="Body">Detailed body (empty for one-line entries).</param>
public sealed record BacklogItem(string Key, string Status, string Title, string Body);
