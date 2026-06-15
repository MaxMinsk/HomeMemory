using System.ComponentModel;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

/// <summary>The Memory MCP tool surface. Each method is a model-callable tool over the note store,
/// authorized against the caller's domain scope. Read tools advertise structured output; expected
/// failures surface to the model as <see cref="McpException"/> messages.</summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private readonly NotesRepository _notes;
    private readonly SchemaRegistry _schemas;
    private readonly DiagnosticsService _diagnostics;
    private readonly IScopeAccessor _scope;
    private readonly SkillsService _skills;

    /// <summary>Creates the tool set over the repository, registry, diagnostics, scope accessor and skills.</summary>
    public MemoryTools(NotesRepository notes, SchemaRegistry schemas, DiagnosticsService diagnostics, IScopeAccessor scope, SkillsService skills)
    {
        _notes = notes;
        _schemas = schemas;
        _diagnostics = diagnostics;
        _scope = scope;
        _skills = skills;
    }

    /// <summary>Searches notes by optional full-text query plus structured filters (scope-restricted, paginated).</summary>
    [McpServerTool(Name = "notes_search", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search notes by an optional full-text query plus filters. Returns ONE page of hits (snippets + BM25 scores, never the full body) with total/hasMore — paginate with offset; do not try to fetch everything at once.")]
    public SearchPage NotesSearch(
        [Description("Full-text query (optional)")] string? query = null,
        [Description("Domain filter, e.g. memory-mcp")] string? domain = null,
        [Description("Type filter, e.g. backlog_item")] string? type = null,
        [Description("Tags; every supplied tag must be present")] string[]? tags = null,
        [Description("Envelope status filter; defaults to active")] string status = "active",
        [Description("Page size, 1-100 (default 20; larger values are clamped)")] int limit = 20,
        [Description("Results to skip for pagination (default 0). Use the returned total/hasMore to page.")] int offset = 0,
        [Description("Optional filter DSL: field op value joined by AND/OR with parentheses. Fields: domain/type/status/title/... and payload.<x>. Ops: == != in. E.g. \"payload.sprint == 'S1' AND payload.status in ('ready','next')\".")] string? filter = null)
        => Translate(() => _notes.Search(query, domain, type, tags, status, limit, offset, Guard().RestrictionForSearch(domain), filter));

    /// <summary>Gets a full note (envelope + payload) by id, if it is in scope.</summary>
    [McpServerTool(Name = "notes_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a full note (envelope + payload) by id.")]
    public Note? NotesGet([Description("Note id")] string id)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? note : null;
    }

    /// <summary>Creates or updates a note, validating the payload against the type schema.</summary>
    [McpServerTool(Name = "notes_upsert", Destructive = false, Idempotent = true, UseStructuredContent = true)]
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
        try
        {
            Guard().Authorize(domain);
            return _notes.Upsert(domain, type, title, body, payload, tags, dedupKey, sourceAgent ?? "mcp");
        }
        catch (NoteValidationException exception)
        {
            // On a schema miss, point the model at any skill that teaches how to author this type.
            throw new McpException(exception.Message + SkillHint(domain, type));
        }
        catch (ScopeForbiddenException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    private string SkillHint(string domain, string type)
    {
        var keys = _skills.List(domain, type).Select(skill => skill.Key).ToArray();
        return keys.Length == 0 ? string.Empty : $" Guidance available — call skill_get for: {string.Join(", ", keys)}.";
    }

    /// <summary>Appends a schema-less free-text journal note (capture-first).</summary>
    [McpServerTool(Name = "notes_append_journal", Destructive = false)]
    [Description("Append a schema-less free-text journal note. Capture-first; structure it later.")]
    public string NotesAppendJournal(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Raw free-text content")] string text,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            Guard().Authorize(domain);
            return _notes.AppendJournal(domain, text, sourceAgent ?? "mcp");
        });

    /// <summary>Creates a directed link from one note to another (both must be in scope).</summary>
    [McpServerTool(Name = "notes_link", Destructive = false)]
    [Description("Create a directed link (rel) from one note to another, e.g. depends_on, derived_from.")]
    public string NotesLink(
        [Description("Source note id")] string fromId,
        [Description("Target note id")] string toId,
        [Description("Relationship verb")] string rel)
        => Translate(() =>
        {
            AuthorizeNote(fromId);
            AuthorizeNote(toId);
            _notes.Link(fromId, toId, rel);
            return "ok";
        });

    /// <summary>Soft-archives a note (no hard delete).</summary>
    [McpServerTool(Name = "notes_archive", Idempotent = true)]
    [Description("Soft-archive a note (status -> archived). No hard delete. Returns true if archived.")]
    public bool NotesArchive([Description("Note id")] string id)
        => Translate(() =>
        {
            AuthorizeNote(id);
            return _notes.Archive(id);
        });

    /// <summary>Marks the old note superseded by the new one and links them.</summary>
    [McpServerTool(Name = "notes_supersede")]
    [Description("Mark the old note superseded by the new one and link them (supersedes).")]
    public bool NotesSupersede(
        [Description("Note being replaced")] string oldId,
        [Description("Replacement note")] string newId)
        => Translate(() =>
        {
            AuthorizeNote(oldId);
            AuthorizeNote(newId);
            return _notes.Supersede(oldId, newId);
        });

    /// <summary>Lists registered note types as type@version.</summary>
    [McpServerTool(Name = "schema_list_types", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List registered note types as type@version strings.")]
    public IReadOnlyList<string> SchemaListTypes() =>
        _schemas.All.Select(definition => $"{definition.Type}@{definition.Version}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    /// <summary>Returns the JSON Schema for a type (latest version), or null.</summary>
    [McpServerTool(Name = "schema_get", ReadOnly = true, OpenWorld = false)]
    [Description("Get the JSON Schema document for a type (latest version), or null if unknown.")]
    public string? SchemaGet([Description("Note type, e.g. backlog_item")] string type) =>
        _schemas.GetLatest(type)?.Json;

    /// <summary>Returns server/database diagnostics.</summary>
    [McpServerTool(Name = "status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
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

    // Translate expected domain failures into MCP errors the model can read and act on
    // (a plain exception would be collapsed to a generic "an error occurred" message).
    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (NoteValidationException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (ScopeForbiddenException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (FilterException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
