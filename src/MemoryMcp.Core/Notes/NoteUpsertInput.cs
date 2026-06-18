namespace MemoryMcp.Core.Notes;

/// <summary>One item of a bulk upsert (MEMP-180): the same fields a single <see cref="NotesWriter.Upsert"/> takes,
/// with payload/tags as JSON strings. The whole batch is applied in one transaction (all-or-nothing).</summary>
/// <param name="Domain">Namespace, e.g. <c>memory-mcp</c>.</param>
/// <param name="Type">Schema type, e.g. <c>backlog_item</c>.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Body">Free-text body.</param>
/// <param name="PayloadJson">Typed payload as JSON.</param>
/// <param name="TagsJson">Tags as a JSON array string.</param>
/// <param name="DedupKey">Stable upsert key.</param>
/// <param name="Project">Optional project sub-axis.</param>
/// <param name="ExpectedRevision">Optional optimistic-concurrency guard (prior <c>updated_utc</c>).</param>
public sealed record NoteUpsertInput(
    string Domain, string Type, string? Title = null, string? Body = null, string? PayloadJson = null,
    string? TagsJson = null, string? DedupKey = null, string? Project = null, string? ExpectedRevision = null);
