namespace MemoryMcp.Core.Artifacts;

/// <summary>A stored artifact: metadata referencing a content-addressed blob, never the bytes
/// themselves. Callers receive a <paramref name="Uri"/> and metadata; bytes are fetched out of band.</summary>
/// <param name="Id">Attachment id.</param>
/// <param name="Domain">Owning namespace (for scope).</param>
/// <param name="Sha256">Content hash of the blob.</param>
/// <param name="Uri">Opaque blob reference, e.g. <c>blob://&lt;sha256&gt;</c>.</param>
/// <param name="NoteId">Note this artifact is attached to, if any.</param>
/// <param name="Filename">Original/display filename, if any.</param>
/// <param name="ContentType">MIME type, if known.</param>
/// <param name="SizeBytes">Blob size in bytes.</param>
/// <param name="CreatedUtc">When the attachment was recorded (ISO-8601 UTC).</param>
public sealed record Artifact(
    string Id, string Domain, string Sha256, string Uri, string? NoteId,
    string? Filename, string? ContentType, long SizeBytes, string CreatedUtc);
