using System.ComponentModel;
using System.Text;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

/// <summary>Server-side ingest root for <c>artifacts_put</c> via <c>sourcePath</c>. Null disables
/// path ingestion (only inline content is accepted), which is the safe default.</summary>
/// <param name="IngestRoot">Absolute directory that <c>sourcePath</c> files must live under.</param>
public sealed record ArtifactIngestOptions(string? IngestRoot);

/// <summary>
/// MCP tools for artifacts (content-addressed blobs + attachment metadata). Honors the project rule
/// that bytes never pass through the model context: inline <c>content</c> is for agent-generated text
/// (e.g. a rendered doc); opaque bytes (photos/PDFs) are ingested from a server-readable file under
/// the configured ingest root. Reads return metadata and a <c>blob://</c> URI, never the bytes.
/// </summary>
[McpServerToolType]
public sealed class ArtifactTools
{
    private readonly ArtifactsService _artifacts;
    private readonly IScopeAccessor _scope;
    private readonly string? _ingestRoot;

    /// <summary>Creates the artifact tools over the service, scope accessor and ingest options.</summary>
    public ArtifactTools(ArtifactsService artifacts, IScopeAccessor scope, ArtifactIngestOptions options)
    {
        _artifacts = artifacts;
        _scope = scope;
        _ingestRoot = options.IngestRoot;
    }

    /// <summary>Stores an artifact (inline text or a server-side file) and returns its metadata + URI.</summary>
    [McpServerTool(Name = "artifacts_put", Destructive = false, UseStructuredContent = true)]
    [Description("Store an artifact; returns metadata + a blob:// URI (never the bytes). Provide EITHER `content` (inline text, e.g. rendered markdown) OR `sourcePath` (a server-readable file under the ingest root, for opaque bytes like photos/PDFs).")]
    public Artifact ArtifactsPut(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Inline text content (use for agent-generated docs)")] string? content = null,
        [Description("Path to a server-readable file under the ingest root (use for opaque bytes)")] string? sourcePath = null,
        [Description("Display/original filename")] string? filename = null,
        [Description("MIME type, e.g. text/markdown")] string? contentType = null,
        [Description("Note id to attach this artifact to")] string? noteId = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            Guard().Authorize(domain);
            var bytes = ResolveBytes(content, sourcePath, ref filename);
            return _artifacts.Put(domain, bytes, filename, contentType, noteId, sourceAgent);
        });

    /// <summary>Gets artifact metadata by id (no bytes), if in scope.</summary>
    [McpServerTool(Name = "artifacts_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get an artifact's metadata and blob:// URI by id (never the bytes).")]
    public Artifact? ArtifactsGet([Description("Artifact id")] string id)
    {
        var artifact = _artifacts.Get(id);
        return artifact is not null && Guard().IsAllowed(artifact.Domain) ? artifact : null;
    }

    /// <summary>Lists artifacts in a domain (optionally for one note), if in scope.</summary>
    [McpServerTool(Name = "artifacts_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List artifacts (metadata + URIs) in a domain, optionally only those attached to a given note.")]
    public IReadOnlyList<Artifact> ArtifactsList(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Only artifacts attached to this note id (optional)")] string? noteId = null)
        => Guard().IsAllowed(domain) ? _artifacts.List(domain, noteId) : Array.Empty<Artifact>();

    private byte[] ResolveBytes(string? content, string? sourcePath, ref string? filename)
    {
        var hasContent = !string.IsNullOrEmpty(content);
        var hasPath = !string.IsNullOrWhiteSpace(sourcePath);
        if (hasContent == hasPath)
        {
            throw new ArtifactException("Provide exactly one of `content` (inline text) or `sourcePath` (server file).");
        }

        if (hasContent)
        {
            return Encoding.UTF8.GetBytes(content!);
        }

        var full = ResolveIngestPath(sourcePath!);
        filename ??= Path.GetFileName(full);
        return File.ReadAllBytes(full);
    }

    // Constrain sourcePath to the configured ingest root (prevents reading arbitrary server files).
    private string ResolveIngestPath(string sourcePath)
    {
        if (_ingestRoot is null)
        {
            throw new ArtifactException("File ingestion is disabled (no ingest root configured); use inline `content`.");
        }

        var root = Path.GetFullPath(_ingestRoot);
        var full = Path.GetFullPath(sourcePath);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArtifactException($"sourcePath must be under the ingest root ({_ingestRoot}).");
        }

        if (!File.Exists(full))
        {
            throw new ArtifactException($"Source file not found under the ingest root: {sourcePath}.");
        }

        return full;
    }

    private ScopeGuard Guard() => new(_scope.Current);

    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArtifactException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (ScopeForbiddenException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
