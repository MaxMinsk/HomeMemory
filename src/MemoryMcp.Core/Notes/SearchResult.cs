namespace MemoryMcp.Core.Notes;

/// <summary>A single search hit: identity, a snippet and score — never the full body.</summary>
/// <param name="Id">Note id.</param>
/// <param name="Title">Note title (may be null).</param>
/// <param name="Snippet">Highlighted text excerpt (null for structured-only queries).</param>
/// <param name="Type">Note type.</param>
/// <param name="Domain">Note domain.</param>
/// <param name="Score">BM25 score for text queries (lower is more relevant); 0 for structured-only queries.</param>
public sealed record SearchResult(string Id, string? Title, string? Snippet, string Type, string Domain, double Score);
