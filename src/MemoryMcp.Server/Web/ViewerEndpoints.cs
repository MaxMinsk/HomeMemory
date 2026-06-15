using System.Reflection;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MemoryMcp.Server.Web;

/// <summary>Read-only web viewer: a self-contained SPA at <c>/ui</c> plus a small JSON API at
/// <c>/api/*</c> over the note store. The SPA loads without a token; its API calls carry the bearer
/// (the same gate as <c>/mcp</c>). A minimal stand-in for the full owner panel.</summary>
internal static class ViewerEndpoints
{
    private static string? _html;

    public static void MapViewer(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/ui"));
        app.MapGet("/ui", () => Results.Content(IndexHtml(), "text/html; charset=utf-8"));

        app.MapGet("/api/stats", (DiagnosticsService diagnostics) => Results.Json(diagnostics.Snapshot()));

        app.MapGet("/api/search", (
            NotesRepository notes,
            string? q, string? domain, string? type, string? tags, string? filter, int? limit, int? offset) =>
        {
            var tagList = string.IsNullOrWhiteSpace(tags)
                ? null
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var page = notes.Search(q, domain, type, tagList, "active",
                limit is > 0 ? limit.Value : 50, offset ?? 0, restrictToDomains: null, filter: filter, includePayload: true);
            return Results.Json(page);
        });

        app.MapGet("/api/notes/{id}", (string id, NotesRepository notes, ArtifactsService artifacts, ArtifactUrlSigner signer) =>
        {
            var note = notes.Get(id);
            if (note is null)
            {
                return Results.NotFound();
            }

            // Each attachment carries a short-lived signed URL; the bearer never goes into the link.
            var attachments = artifacts.List(note.Domain, id)
                .Select(a => new { a.Id, a.Filename, a.ContentType, a.SizeBytes, a.Sha256, url = signer.BuildPath(a.Id) });
            return Results.Json(new { note, attachments, links = notes.Links(id) });
        });

        app.MapArtifactBytes();
    }

    // Artifact byte transfer (out-of-band, never through the model context): signed PUT upload + GET serve.
    private static void MapArtifactBytes(this IEndpointRouteBuilder app)
    {
        // Upload opaque bytes directly via a signed capability URL (from artifacts_request_upload). The
        // signature (verified in middleware) binds the destination, so we trust the query parameters here.
        // Bytes go straight to disk, never through the model context. Kestrel caps the body size.
        app.MapPut("/artifacts/upload", async (HttpRequest request, ArtifactsService artifacts) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return Results.BadRequest("Empty upload body.");
            }

            var query = request.Query;
            var contentType = string.IsNullOrEmpty(query["contentType"]) ? null : query["contentType"].ToString();
            var noteId = string.IsNullOrEmpty(query["noteId"]) ? null : query["noteId"].ToString();
            try
            {
                var artifact = artifacts.Put(query["domain"].ToString(), bytes, query["filename"].ToString(), contentType, noteId, "upload");
                return Results.Json(artifact);
            }
            catch (ArtifactException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        });

        // Serve an artifact's bytes to the browser (rendered HTML renders inline, md/text shows as text).
        // The bytes go to the human, never back through the model context. Auth: bearer header or ?t= token.
        app.MapGet("/artifacts/{id}", (string id, ArtifactsService artifacts, BlobStore blobs) =>
        {
            var artifact = artifacts.Get(id);
            if (artifact is null)
            {
                return Results.NotFound();
            }

            var bytes = blobs.Read(artifact.Sha256);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            // Text artifacts are UTF-8; without an explicit charset the browser guesses (Latin-1) and mangles Cyrillic.
            var contentType = artifact.ContentType ?? "application/octet-stream";
            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("charset", StringComparison.OrdinalIgnoreCase))
            {
                contentType += "; charset=utf-8";
            }

            return Results.File(bytes, contentType);
        });
    }

    private static string IndexHtml()
    {
        if (_html is not null)
        {
            return _html;
        }

        var assembly = typeof(ViewerEndpoints).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".index.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        _html = reader.ReadToEnd();
        return _html;
    }
}
