using System.Reflection;
using System.Security.Cryptography;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MemoryMcp.Server.Web;

/// <summary>Body for creating a per-agent token from the admin UI (MEMP-141).</summary>
/// <param name="Label">Optional human label.</param>
/// <param name="Domains">Comma-separated domains, or <c>*</c> for all (defaults to <c>*</c>).</param>
internal sealed record TokenCreateRequest(string? Label, string? Domains);

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
        app.MapHealth();

        app.MapGet("/api/stats", (DiagnosticsService diagnostics) => Results.Json(diagnostics.Snapshot()));
        app.MapLint();
        app.MapSuggestionActions();
        app.MapAdminActions();
        app.MapTokenAdmin();

        app.MapGet("/api/search", (
            NotesRepository notes, RequestAuthorizer authz,
            string? q, string? domain, string? type, string? tags, string? filter, string? status, int? limit, int? offset, string? sort) =>
        {
            IReadOnlyCollection<string>? restrict;
            try
            {
                restrict = authz.ReadRestriction(domain); // 403 if an explicit out-of-scope domain
            }
            catch (ScopeForbiddenException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tagList = string.IsNullOrWhiteSpace(tags)
                ? null
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var page = notes.Search(q, domain, type, tagList, string.IsNullOrWhiteSpace(status) ? "active" : status,
                limit is > 0 ? limit.Value : 50, offset ?? 0, restrictToDomains: restrict, filter: filter, includePayload: true, sort: sort);
            return Results.Json(page);
        });

        app.MapGet("/api/notes/{id}", (string id, NotesRepository notes, ArtifactsService artifacts, ArtifactUrlSigner signer, RequestAuthorizer authz) =>
        {
            var note = notes.Get(id);
            if (note is null || !authz.CanRead(note.Domain))
            {
                return Results.NotFound(); // out-of-scope notes are indistinguishable from missing
            }

            // Each attachment carries a short-lived signed URL; the bearer never goes into the link.
            var attachments = artifacts.List(note.Domain, id)
                .Select(a => new { a.Id, a.Filename, a.ContentType, a.SizeBytes, a.Sha256, url = signer.BuildPath(a.Id) });
            return Results.Json(new { note, attachments, links = notes.Links(id) });
        });

        app.MapArtifactBytes();
    }

    // Unauthenticated liveness/readiness probe (MEMP-144) for the HA watchdog / Docker healthcheck.
    // 200 only if the database answers; no version or other detail is leaked to an unauthenticated caller.
    private static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (ISqliteConnectionFactory factory) =>
        {
            try
            {
                using var connection = factory.Create();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                command.ExecuteScalar();
                return Results.Json(new { status = "ok" });
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    // Owner maintenance actions (MEMP-140): the CLI ops (normalize-identifiers, gc-blobs) exposed as
    // ROOT-ONLY endpoints so the owner can run them from the UI (the add-on container has no shell).
    // Dry-run by default (?apply=true to write); each returns the same report the CLI prints.
    private static void MapAdminActions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/normalize-identifiers", (RequestAuthorizer authz, ISqliteConnectionFactory factory, bool? apply) =>
            RequireRoot(authz) ?? Results.Json(IdentifierBackfill.Run(factory, apply ?? false)));
        app.MapPost("/api/admin/gc-blobs", (RequestAuthorizer authz, ISqliteConnectionFactory factory, BlobStore blobs, bool? apply) =>
            RequireRoot(authz) ?? Results.Json(BlobReconciler.Reconcile(factory, blobs, apply ?? false)));
        app.MapPost("/api/admin/backfill-project", (RequestAuthorizer authz, ISqliteConnectionFactory factory, bool? apply) =>
            RequireRoot(authz) ?? Results.Json(ProjectBackfill.Run(factory, apply ?? false)));
    }

    // Maintenance is global and mutating — only the unrestricted (root/all-domains) token may run it.
    private static IResult? RequireRoot(RequestAuthorizer authz) =>
        authz.Scope.IsUnrestricted ? null : Results.StatusCode(StatusCodes.Status403Forbidden);

    // Per-agent token management (MEMP-141), root-only. List never exposes hashes; create returns the raw
    // token exactly once (the store keeps only its hash). Minting credentials is owner-only by design.
    private static void MapTokenAdmin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/tokens", (RequestAuthorizer authz, TokenStore tokens) =>
            RequireRoot(authz) ?? Results.Json(tokens.List()));

        app.MapPost("/api/admin/tokens", (RequestAuthorizer authz, TokenStore tokens, TokenCreateRequest body) =>
        {
            if (RequireRoot(authz) is { } denied)
            {
                return denied;
            }

            var domains = string.IsNullOrWhiteSpace(body.Domains) ? "*" : body.Domains.Trim();
            var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var record = tokens.Add(string.IsNullOrWhiteSpace(body.Label) ? null : body.Label.Trim(), domains, raw);
            return Results.Json(new { record, token = raw }); // raw shown once; only the hash is stored
        });

        app.MapPost("/api/admin/tokens/{id}/revoke", (string id, RequestAuthorizer authz, TokenStore tokens) =>
            RequireRoot(authz) ?? Results.Json(new { ok = true, revoked = tokens.Revoke(id) }));
    }

    // Review actions for memory_evolution_suggestion notes (MEMP-133): the only write path in the viewer.
    // Apply maps proposed_patch/proposed_links onto the target (scope-checked, optimistic concurrency, all
    // audited, non-destructive); Reject just marks the suggestion. Bearer-gated like every other endpoint.
    private static void MapSuggestionActions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/suggestions/{id}/apply",
            (string id, SuggestionReviewer reviewer, RequestAuthorizer authz) => ToResult(reviewer.Apply(id, authz.Scope)));
        app.MapPost("/api/suggestions/{id}/reject",
            (string id, SuggestionReviewer reviewer, RequestAuthorizer authz) => ToResult(reviewer.Reject(id, authz.Scope)));
    }

    private static IResult ToResult(SuggestionActionResult result) => result.Outcome switch
    {
        SuggestionOutcome.Applied => Results.Json(new { ok = true, applied = true, result.Data }),
        SuggestionOutcome.Rejected => Results.Json(new { ok = true, rejected = true }),
        SuggestionOutcome.NotFound => Results.NotFound(),
        SuggestionOutcome.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        SuggestionOutcome.Conflict => Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(result.Message ?? "Bad request", statusCode: StatusCodes.Status400BadRequest),
    };

    // Memory inbox (MEMP-110): the read-only review queue — lint findings (unstructured/stale/secret/
    // no-tags/duplicate/broken-link) the owner should triage. Mutating fixes go through the MCP tools
    // (two-phase for destructive ones), not this read-only API.
    private static void MapLint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lint", (RequestAuthorizer authz, ISqliteConnectionFactory factory, TimeProvider clock, string? domain, int? limit) =>
        {
            IReadOnlyCollection<string>? restrict;
            try
            {
                restrict = authz.ReadRestriction(domain);
            }
            catch (ScopeForbiddenException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var findings = new NotesLinter(factory, clock).Lint(domain, restrict, limit is > 0 ? limit.Value : 200);
            return Results.Json(findings);
        });
    }

    // Artifact byte transfer (out-of-band, never through the model context): signed PUT upload + GET serve.
    private static void MapArtifactBytes(this IEndpointRouteBuilder app)
    {
        app.MapArtifactUpload();
        app.MapArtifactServe();
    }

    private static void MapArtifactUpload(this IEndpointRouteBuilder app)
    {
        // Upload opaque bytes directly via a signed capability URL (from artifacts_request_upload). The
        // signature (verified in middleware) binds the destination, so we trust the query parameters here.
        // Bytes go straight to disk, never through the model context. Kestrel caps the body size.
        app.MapPut("/artifacts/upload", async (HttpRequest request, ArtifactsService artifacts, RequestAuthorizer authz) =>
        {
            var query = request.Query;
            // Bearer requests must be in scope for the destination domain; a signed URL is already a capability.
            if (!authz.CanRead(query["domain"].ToString()))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return Results.BadRequest("Empty upload body.");
            }

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
    }

    private static void MapArtifactServe(this IEndpointRouteBuilder app)
    {
        // Serve an artifact's bytes to the browser (rendered HTML renders inline, md/text shows as text).
        // The bytes go to the human, never back through the model context. Auth: bearer header or ?t= token.
        app.MapGet("/artifacts/{id}", (string id, ArtifactsService artifacts, BlobStore blobs, RequestAuthorizer authz) =>
        {
            var artifact = artifacts.Get(id);
            // Bearer requests must be in scope for the artifact's domain; a signed URL is already a capability.
            if (artifact is null || !authz.CanRead(artifact.Domain))
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
