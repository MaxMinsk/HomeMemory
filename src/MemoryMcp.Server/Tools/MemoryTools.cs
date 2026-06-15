using System.ComponentModel;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

/// <summary>The Memory MCP tool surface. Each method is a model-callable tool over the note store,
/// authorized against the caller's domain scope.</summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private readonly NotesRepository _notes;
    private readonly SchemaRegistry _schemas;
    private readonly DiagnosticsService _diagnostics;
    private readonly IScopeAccessor _scope;

    /// <summary>Creates the tool set over the repository, registry, diagnostics and scope accessor.</summary>
    public MemoryTools(NotesRepository notes, SchemaRegistry schemas, DiagnosticsService diagnostics, IScopeAccessor scope)
    {
        _notes = notes;
        _schemas = schemas;
        _diagnostics = diagnostics;
        _scope = scope;
    }

    /// <summary>Searches notes by optional full-text query plus structured filters (scope-restricted).</summary>
    [McpServerTool(Name = "notes_search")]
    [Description("Search notes by an optional full-text query plus filters. Returns snippets and BM25 scores, never the full body.")]
    public IReadOnlyList<SearchResult> NotesSearch(
        [Description("Full-text query (optional)")] string? query = null,
        [Description("Domain filter, e.g. memory-mcp")] string? domain = null,
        [Description("Type filter, e.g. backlog_item")] string? type = null,
        [Description("Tags; every supplied tag must be present")] string[]? tags = null,
        [Description("Envelope status filter; defaults to active")] string status = "active",
        [Description("Maximum number of results")] int limit = 10)
    {
        var restriction = Guard().RestrictionForSearch(domain);
        return _notes.Search(query, domain, type, tags, status, limit, restriction);
    }

    /// <summary>Gets a full note (envelope + payload) by id, if it is in scope.</summary>
    [McpServerTool(Name = "notes_get")]
    [Description("Get a full note (envelope + payload) by id.")]
    public Note? NotesGet([Description("Note id")] string id)
    {
        var note = _notes.Get(id);
        if (note is null || !Guard().IsAllowed(note.Domain))
        {
            return null;
        }

        return note;
    }

    /// <summary>Creates or updates a note, validating the payload against the type schema.</summary>
    [McpServerTool(Name = "notes_upsert")]
    [Description("Create or update a note by (domain, type, dedup_key). Validates payload against the type schema.")]
    public UpsertResult NotesUpsert(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Schema type, e.g. backlog_item")] string type,
        [Description("Human-readable title")] string? title = null,
        [Description("Free-text body")] string? body = null,
        [Description("Typed payload as JSON")] string? payload = null,
        [Description("Tags as a JSON array string")] string? tags = null,
        [Description("Stable upsert key (e.g. MEMP-001)")] string? dedupKey = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
    {
        Guard().Authorize(domain);
        return _notes.Upsert(domain, type, title, body, payload, tags, dedupKey, sourceAgent ?? "mcp");
    }

    /// <summary>Appends a schema-less free-text journal note (capture-first).</summary>
    [McpServerTool(Name = "notes_append_journal")]
    [Description("Append a schema-less free-text journal note. Capture-first; structure it later.")]
    public string NotesAppendJournal(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Raw free-text content")] string text,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
    {
        Guard().Authorize(domain);
        return _notes.AppendJournal(domain, text, sourceAgent ?? "mcp");
    }

    /// <summary>Creates a directed link from one note to another (both must be in scope).</summary>
    [McpServerTool(Name = "notes_link")]
    [Description("Create a directed link (rel) from one note to another, e.g. depends_on, derived_from.")]
    public string NotesLink(
        [Description("Source note id")] string fromId,
        [Description("Target note id")] string toId,
        [Description("Relationship verb")] string rel)
    {
        AuthorizeNote(fromId);
        AuthorizeNote(toId);
        _notes.Link(fromId, toId, rel);
        return "ok";
    }

    /// <summary>Soft-archives a note (no hard delete).</summary>
    [McpServerTool(Name = "notes_archive")]
    [Description("Soft-archive a note (status -> archived). No hard delete. Returns true if archived.")]
    public bool NotesArchive([Description("Note id")] string id)
    {
        AuthorizeNote(id);
        return _notes.Archive(id);
    }

    /// <summary>Marks the old note superseded by the new one and links them.</summary>
    [McpServerTool(Name = "notes_supersede")]
    [Description("Mark the old note superseded by the new one and link them (supersedes).")]
    public bool NotesSupersede(
        [Description("Note being replaced")] string oldId,
        [Description("Replacement note")] string newId)
    {
        AuthorizeNote(oldId);
        AuthorizeNote(newId);
        return _notes.Supersede(oldId, newId);
    }

    /// <summary>Lists registered note types as type@version.</summary>
    [McpServerTool(Name = "schema_list_types")]
    [Description("List registered note types as type@version strings.")]
    public IReadOnlyList<string> SchemaListTypes() =>
        _schemas.All.Select(definition => $"{definition.Type}@{definition.Version}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    /// <summary>Returns the JSON Schema for a type (latest version), or null.</summary>
    [McpServerTool(Name = "schema_get")]
    [Description("Get the JSON Schema document for a type (latest version), or null if unknown.")]
    public string? SchemaGet([Description("Note type, e.g. backlog_item")] string type) =>
        _schemas.GetLatest(type)?.Json;

    /// <summary>Returns server/database diagnostics.</summary>
    [McpServerTool(Name = "status")]
    [Description("Server and database diagnostics: schema version, registered schemas, note count, search backend.")]
    public StatusReport Status() => _diagnostics.Snapshot();

    private ScopeGuard Guard() => new(_scope.Current);

    private void AuthorizeNote(string id)
    {
        var note = _notes.Get(id);
        if (note is not null)
        {
            Guard().Authorize(note.Domain);
        }
    }
}
