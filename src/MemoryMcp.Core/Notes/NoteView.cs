using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// A note returned by <c>notes_get</c>: the full envelope/payload plus cheap size metadata so a caller
/// can decide whether (and how much of) the body to pull. The same field names as <see cref="Note"/>,
/// so existing readers keep working; <see cref="Body"/> reflects what was actually requested
/// (omitted when <c>includeBody=false</c>, or cut to <c>bodyMaxChars</c>), while <see cref="BodyChars"/>
/// always reports the true length.
/// </summary>
/// <param name="Id">Stable id.</param>
/// <param name="Domain">Namespace.</param>
/// <param name="Type">Schema discriminator.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Body">Body as returned (null if not requested; truncated if bodyMaxChars was set).</param>
/// <param name="PayloadJson">Typed payload as JSON.</param>
/// <param name="TagsJson">Tags as a JSON array string.</param>
/// <param name="DedupKey">Stable upsert key (may be null).</param>
/// <param name="Status">Envelope status: active / archived / superseded.</param>
/// <param name="CreatedUtc">Creation timestamp (ISO-8601 UTC).</param>
/// <param name="UpdatedUtc">Last-update timestamp (ISO-8601 UTC) — the revision/etag.</param>
/// <param name="SourceAgent">Provenance: who last wrote it.</param>
/// <param name="SchemaVer">Schema version the payload was validated against.</param>
/// <param name="Deleted">Soft-delete flag.</param>
/// <param name="BodyChars">True length of the stored body in characters (regardless of what Body returns).</param>
/// <param name="Truncated">True when the returned Body is shorter than the stored body.</param>
/// <param name="AttachmentCount">Number of artifacts attached to this note.</param>
/// <param name="LinkCount">Number of links touching this note (both directions).</param>
/// <param name="Project">Optional project sub-axis within the domain (null = none).</param>
/// <param name="Payload">The payload already parsed to JSON (null when absent/unparseable) — so a caller needn't re-parse <see cref="PayloadJson"/>.</param>
/// <param name="Tags">The tags already parsed to an array (null when absent/unparseable) — so a caller needn't re-parse <see cref="TagsJson"/>.</param>
public sealed record NoteView(
    string Id, string Domain, string Type, string? Title, string? Body,
    string? PayloadJson, string? TagsJson, string? DedupKey, string Status,
    string CreatedUtc, string UpdatedUtc, string? SourceAgent, int SchemaVer, bool Deleted,
    int BodyChars, bool Truncated, int AttachmentCount, int LinkCount, string? Project = null,
    JsonElement? Payload = null, IReadOnlyList<string>? Tags = null)
{
    /// <summary>Builds a view from a full note, applying the requested body windowing and the counts.</summary>
    public static NoteView From(Note note, bool includeBody, int? bodyMaxChars, int attachmentCount, int linkCount)
    {
        var fullBody = note.Body;
        var bodyChars = fullBody?.Length ?? 0;

        string? body;
        bool truncated;
        if (!includeBody)
        {
            body = null;
            truncated = bodyChars > 0;
        }
        else if (bodyMaxChars is int cap && cap >= 0 && bodyChars > cap)
        {
            body = fullBody!.Substring(0, cap);
            truncated = true;
        }
        else
        {
            body = fullBody;
            truncated = false;
        }

        return new NoteView(
            note.Id, note.Domain, note.Type, note.Title, body,
            note.PayloadJson, note.TagsJson, note.DedupKey, note.Status,
            note.CreatedUtc, note.UpdatedUtc, note.SourceAgent, note.SchemaVer, note.Deleted,
            bodyChars, truncated, attachmentCount, linkCount, note.Project,
            ParseObject(note.PayloadJson), ParseTags(note.TagsJson));
    }

    // Parses the payload to a detached JsonElement (Clone survives the JsonDocument disposal); null if absent/invalid.
    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Parses the tags JSON array to a string list; null if absent/invalid.
    private static IReadOnlyList<string>? ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
