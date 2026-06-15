namespace MemoryMcp.Core.Notes;

/// <summary>A single search hit: identity, a snippet and score — never the full body.</summary>
/// <param name="Id">Note id.</param>
/// <param name="Title">Note title (may be null).</param>
/// <param name="Snippet">Highlighted text excerpt (null for structured-only queries).</param>
/// <param name="Type">Note type.</param>
/// <param name="Domain">Note domain.</param>
/// <param name="Score">BM25 score for text queries (lower is more relevant); 0 for structured-only queries.</param>
/// <param name="Status">Envelope status; populated only when the caller requests payload (else null).</param>
/// <param name="PayloadJson">Typed payload as JSON; populated only when the caller requests payload (else null).</param>
/// <param name="TagsJson">Tags as a JSON array string; populated only when the caller requests payload (else null).</param>
/// <param name="DedupKey">Stable upsert key; populated only when the caller requests payload (else null).</param>
/// <param name="UpdatedUtc">Last-update timestamp; populated only when the caller requests payload (else null).</param>
public sealed record SearchResult(
    string Id, string? Title, string? Snippet, string Type, string Domain, double Score,
    string? Status = null, string? PayloadJson = null, string? TagsJson = null,
    string? DedupKey = null, string? UpdatedUtc = null);
