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

        app.MapGet("/api/notes/{id}", (string id, NotesRepository notes, ArtifactsService artifacts) =>
        {
            var note = notes.Get(id);
            return note is null
                ? Results.NotFound()
                : Results.Json(new { note, attachments = artifacts.List(note.Domain, id) });
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
