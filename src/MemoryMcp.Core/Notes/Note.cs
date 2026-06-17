namespace MemoryMcp.Core.Notes;

/// <summary>A full note: the relational envelope plus its typed payload (as JSON).</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Domain">Namespace.</param>
/// <param name="Type">Schema discriminator.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Body">Free-text body.</param>
/// <param name="PayloadJson">Typed payload as JSON.</param>
/// <param name="TagsJson">Tags as a JSON array string.</param>
/// <param name="DedupKey">Stable upsert key (may be null).</param>
/// <param name="Status">Envelope status: active / archived / superseded.</param>
/// <param name="CreatedUtc">Creation timestamp (ISO-8601 UTC).</param>
/// <param name="UpdatedUtc">Last-update timestamp (ISO-8601 UTC).</param>
/// <param name="SourceAgent">Provenance: who last wrote it.</param>
/// <param name="SchemaVer">Schema version the payload was validated against.</param>
/// <param name="Deleted">Soft-delete flag.</param>
/// <param name="Project">Optional project sub-axis within the domain (null = none).</param>
public sealed record Note(
    string Id, string Domain, string Type, string? Title, string? Body,
    string? PayloadJson, string? TagsJson, string? DedupKey, string Status,
    string CreatedUtc, string UpdatedUtc, string? SourceAgent, int SchemaVer, bool Deleted, string? Project = null);
