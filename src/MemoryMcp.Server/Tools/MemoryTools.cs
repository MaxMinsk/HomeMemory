using System.ComponentModel;
using System.Text.Json;
using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
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
    private readonly ConfirmationService _confirmations;
    private readonly ISqliteConnectionFactory _connectionFactory;

    /// <summary>Creates the tool set over the repository, registry, diagnostics, scope accessor, skills, confirmations and database.</summary>
    public MemoryTools(NotesRepository notes, SchemaRegistry schemas, DiagnosticsService diagnostics, IScopeAccessor scope,
        SkillsService skills, ConfirmationService confirmations, ISqliteConnectionFactory connectionFactory)
    {
        _notes = notes;
        _schemas = schemas;
        _diagnostics = diagnostics;
        _scope = scope;
        _skills = skills;
        _confirmations = confirmations;
        _connectionFactory = connectionFactory;
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
        [Description("Optional filter DSL: field op value joined by AND/OR with parentheses. Fields: domain/type/status/title/... and payload.<x>. Ops: == != in, and 'is null'/'is not null'. E.g. \"payload.sprint is null\" (general backlog) or \"payload.sprint == 'S1' AND payload.status in ('ready','next')\".")] string? filter = null,
        [Description("When true, each hit also includes its envelope status and payload JSON (still no body), so you can render a board without a get per row.")] bool includePayload = false,
        [Description("When true, each hit also includes its links (both directions), so you can render a graph without a notes_links call per row.")] bool includeLinks = false)
        => Translate(() => _notes.Search(query, domain, type, tags, status, limit, offset, Guard().RestrictionForSearch(domain), filter, includePayload, includeLinks));

    /// <summary>Recalls a compact context block (hits + linked neighbors) for a query, scope-restricted.</summary>
    [McpServerTool(Name = "notes_recall", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Recall a prompt-ready context block for a query: the top matching notes (FTS, with payload) PLUS their one-hop linked neighbors (both directions), scope-restricted, with relation labels and source ids/revisions — a case's surrounding context in one call instead of search + many gets. Snippets only, never full bodies (use notes_read/notes_get for those).")]
    public RecallResult NotesRecall(
        [Description("Full-text query")] string query,
        [Description("Domain filter (optional)")] string? domain = null,
        [Description("Max primary hits (default 10)")] int limit = 10,
        [Description("Include one-hop linked neighbors of the hits (default true)")] bool includeLinks = true,
        [Description("Graph expansion depth (currently one hop)")] int maxHops = 1)
        => Translate(() => _notes.Recall(query, domain, limit, Guard().RestrictionForSearch(domain), includeLinks, maxHops));

    /// <summary>Gets a note (envelope + payload + size metadata) by id, if it is in scope.</summary>
    [McpServerTool(Name = "notes_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note (envelope + payload) by id, plus size metadata (bodyChars, attachmentCount, linkCount). By default the full body is returned. For a large note, pass includeBody=false to peek (no body) or bodyMaxChars to cap it — check `truncated`/`bodyChars` and use notes_read/notes_outline to read the rest.")]
    public NoteView? NotesGet(
        [Description("Note id")] string id,
        [Description("Include the body text (default true). Pass false to peek: envelope + payload + counts, no body.")] bool includeBody = true,
        [Description("Cap the returned body to this many characters (optional; default = full body). Sets truncated=true when it cuts.")] int? bodyMaxChars = null)
    {
        var view = _notes.GetView(id, includeBody, bodyMaxChars);
        return view is not null && Guard().IsAllowed(view.Domain) ? view : null;
    }

    /// <summary>Reads a windowed slice of a note's body, if it is in scope.</summary>
    [McpServerTool(Name = "notes_read", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a slice of a note's body: returns content from `offset` for up to `limitChars` chars (default 4000, max 20000), plus totalChars/returnedChars/truncated and a nextOffset cursor + the revision (updatedUtc). Page a large body by following nextOffset; for a section, get its offset from notes_outline. Cheaper than notes_get for big notes.")]
    public NoteReadSlice? NotesRead(
        [Description("Note id")] string id,
        [Description("Start character offset (default 0)")] int offset = 0,
        [Description("Max characters to return (default 4000, clamped to 20000)")] int limitChars = NotesReader.DefaultReadChars)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? _notes.ReadBody(id, offset, limitChars) : null;
    }

    /// <summary>Returns the Markdown heading map of a note's body (for section reads), if it is in scope.</summary>
    [McpServerTool(Name = "notes_outline", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note's Markdown heading map: each heading's level, text and character offset, plus totalChars. To read one section, call notes_read(offset = that heading's offset, limitChars = next heading's offset minus it).")]
    public NoteOutline? NotesOutline([Description("Note id")] string id)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? _notes.Outline(id) : null;
    }

    /// <summary>Finds a substring within one note's body (case-insensitive), if it is in scope.</summary>
    [McpServerTool(Name = "notes_find", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find a substring within ONE note's body (case-insensitive), like ripgrep on a file: returns each match's character offset and a surrounding context window, plus matchCount/truncated. Use a match's offset with notes_read to read around it — avoids pulling the whole body to locate something.")]
    public NoteFindResult? NotesFind(
        [Description("Note id")] string id,
        [Description("Substring to search for")] string query,
        [Description("Characters of context each side of a match (default 80, max 500)")] int contextChars = NotesReader.DefaultContextChars,
        [Description("Max matches to return (default 10, max 100)")] int limit = NotesReader.DefaultFindMatches)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? _notes.Find(id, query, contextChars, limit) : null;
    }

    /// <summary>Creates or updates a note, validating the payload against the type schema.</summary>
    [McpServerTool(Name = "notes_upsert", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Create or update a note by (domain, type, dedup_key). Validates payload against the type schema.")]
    public UpsertResult NotesUpsert(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Schema type, e.g. backlog_item")] string type,
        [Description("Human-readable title")] string? title = null,
        [Description("Free-text body")] string? body = null,
        [Description("Typed payload as a JSON object (a JSON string is also accepted)")] JsonElement? payload = null,
        [Description("Tags as a JSON array (a JSON array string is also accepted)")] JsonElement? tags = null,
        [Description("Stable upsert key (e.g. MEMP-001)")] string? dedupKey = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
    {
        try
        {
            Guard().Authorize(domain);
            return _notes.Upsert(domain, type, title, body, JsonArg(payload), JsonArg(tags), dedupKey, sourceAgent ?? "mcp");
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

    /// <summary>Creates a note and its outgoing links atomically (all-or-nothing).</summary>
    [McpServerTool(Name = "notes_assemble", Destructive = false, UseStructuredContent = true)]
    [Description("Create (or upsert by dedupKey) a note AND its outgoing links in ONE transaction — all-or-nothing. If any link's target is missing or its rel invalid, nothing is created (no half-built case). `links` is an array of {toId, rel} with an active-voice lower_snake_case rel. payload/tags accept an object/array or a JSON string.")]
    public AssembleResult NotesAssemble(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Schema type, e.g. backlog_item")] string type,
        [Description("Human-readable title")] string? title = null,
        [Description("Free-text body")] string? body = null,
        [Description("Typed payload as a JSON object (a JSON string is also accepted)")] JsonElement? payload = null,
        [Description("Tags as a JSON array (a JSON array string is also accepted)")] JsonElement? tags = null,
        [Description("Stable upsert key (e.g. MEMP-001)")] string? dedupKey = null,
        [Description("Outgoing links to create with the note: [{toId, rel}]")] AssembleLink[]? links = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            Guard().Authorize(domain);
            foreach (var link in links ?? Array.Empty<AssembleLink>())
            {
                AuthorizeNote(link.ToId);
            }

            return _notes.Assemble(domain, type, title, body, JsonArg(payload), JsonArg(tags), dedupKey, links, sourceAgent ?? "mcp");
        });

    /// <summary>Appends a schema-less free-text journal note (capture-first).</summary>
    [McpServerTool(Name = "notes_append_journal", Destructive = false)]
    [Description("Append a schema-less free-text journal note (capture-first; structure it later). A title is derived from the first line if you omit one, a stable dedupKey is assigned, and the note is tagged 'unstructured' so it can be found for later structuring.")]
    public string NotesAppendJournal(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Raw free-text content")] string text,
        [Description("Optional title (default: derived from the first line)")] string? title = null,
        [Description("Optional tags as a JSON array string ('unstructured' is always added)")] string? tags = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            Guard().Authorize(domain);
            return _notes.AppendJournal(domain, text, title, tags, sourceAgent ?? "mcp");
        });

    /// <summary>Creates a directed link from one note to another (both must be in scope).</summary>
    [McpServerTool(Name = "notes_link", Destructive = false)]
    [Description("Create a directed link (rel) from one note to another, e.g. depends_on, derived_from.")]
    public string NotesLink(
        [Description("Source note id")] string fromId,
        [Description("Target note id")] string toId,
        [Description("Relationship verb (active-voice lower_snake_case, e.g. uses, depends_on, in_sprint)")] string rel)
        => Translate(() =>
        {
            if (!RelationName.IsValid(rel))
            {
                throw new McpException($"Invalid relation '{rel}': {RelationName.Expectation}.");
            }

            AuthorizeNote(fromId);
            AuthorizeNote(toId);
            _notes.Link(fromId, toId, rel);
            return "ok";
        });

    /// <summary>Requests a soft-archive; returns a pending confirmation token (apply via notes_confirm).</summary>
    [McpServerTool(Name = "notes_archive", Destructive = false, UseStructuredContent = true)]
    [Description("Request to soft-archive a note. Does NOT archive immediately — returns a confirmation token; call notes_confirm to apply it (or notes_cancel to drop it).")]
    public PendingAction NotesArchive([Description("Note id")] string id)
        => Translate(() =>
        {
            AuthorizeNote(id);
            return _confirmations.Request("archive", id, null, $"archive note {id}", null);
        });

    /// <summary>Requests a supersede; returns a pending confirmation token (apply via notes_confirm).</summary>
    [McpServerTool(Name = "notes_supersede", Destructive = false, UseStructuredContent = true)]
    [Description("Request to mark the old note superseded by the new one. Does NOT apply immediately — returns a confirmation token; call notes_confirm to apply it.")]
    public PendingAction NotesSupersede(
        [Description("Note being replaced")] string oldId,
        [Description("Replacement note")] string newId)
        => Translate(() =>
        {
            AuthorizeNote(oldId);
            AuthorizeNote(newId);
            return _confirmations.Request("supersede", oldId, newId, $"supersede {oldId} with {newId}", null);
        });

    /// <summary>Confirms and executes a previously requested destructive action (exactly once).</summary>
    [McpServerTool(Name = "notes_confirm", Destructive = true, UseStructuredContent = true)]
    [Description("Confirm and execute a destructive action requested earlier (archive / supersede / artifact delete) by its token. A token executes at most once.")]
    public ConfirmationResult NotesConfirm([Description("Confirmation token from notes_archive/notes_supersede")] string token)
        => Translate(() => _confirmations.Confirm(token, Guard().WriteRestriction(), null));

    /// <summary>Cancels a pending destructive action so it can never execute.</summary>
    [McpServerTool(Name = "notes_cancel", Idempotent = true, UseStructuredContent = true)]
    [Description("Cancel a pending destructive action by its token so it can never execute.")]
    public ConfirmationResult NotesCancel([Description("Confirmation token")] string token)
        => Translate(() => _confirmations.Cancel(token, Guard().WriteRestriction(), null));

    /// <summary>Partially updates a note by id (merge), with optional optimistic-concurrency check.</summary>
    [McpServerTool(Name = "notes_patch", Destructive = false, UseStructuredContent = true)]
    [Description("Update a note by id WITHOUT replacing it: only the fields you pass change, and a `payload` is shallow-merged into the existing one (its keys win). Pass `expectedUpdatedUtc` (the revision from notes_get/upsert) to fail safely if another agent changed it meanwhile. Returns the updated note.")]
    public Note NotesPatch(
        [Description("Note id")] string id,
        [Description("New title (optional)")] string? title = null,
        [Description("New body (optional)")] string? body = null,
        [Description("Payload keys to merge, as a JSON object (a JSON string is also accepted)")] JsonElement? payload = null,
        [Description("Tags as a JSON array that replaces tags (a JSON array string is also accepted)")] JsonElement? tags = null,
        [Description("Expected current updated_utc for optimistic concurrency (optional)")] string? expectedUpdatedUtc = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            AuthorizeNote(id);
            return _notes.Patch(id, title, body, JsonArg(payload), JsonArg(tags), expectedUpdatedUtc, sourceAgent ?? "mcp")
                ?? throw new McpException($"Note '{id}' not found.");
        });

    /// <summary>Restores an archived/superseded note to active.</summary>
    [McpServerTool(Name = "notes_restore", Idempotent = true)]
    [Description("Restore an archived or superseded note back to active. Returns true if a note was restored.")]
    public bool NotesRestore([Description("Note id")] string id)
        => Translate(() =>
        {
            AuthorizeNote(id);
            return _notes.Restore(id);
        });

    /// <summary>Suggests notes related to a given note by shared tags (scope-restricted).</summary>
    [McpServerTool(Name = "notes_related", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find notes related to a given note by shared tags (most overlap first), scope-restricted — a linking/dedup suggestion. Returns each candidate's id/title/type/domain and the shared tags. Creating a link stays explicit (notes_link / notes_assemble).")]
    public IReadOnlyList<RelatedNote> NotesRelated(
        [Description("Note id")] string id,
        [Description("Max candidates (default 10)")] int limit = 10)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain)
            ? _notes.Related(id, limit, Guard().RestrictionForSearch(null))
            : Array.Empty<RelatedNote>();
    }

    /// <summary>Lists a note's links in both directions (each resolved to the note at the other end).</summary>
    [McpServerTool(Name = "notes_links", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List a note's links (both directions): each entry has direction (out/in), rel, and the other note's id/title/type.")]
    public IReadOnlyList<LinkView> NotesLinks([Description("Note id")] string id)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? _notes.Links(id) : Array.Empty<LinkView>();
    }

    /// <summary>Removes a directed link from one note to another.</summary>
    [McpServerTool(Name = "notes_unlink", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Remove a directed link (rel) from one note to another. Returns the number of links removed (0 if none matched).")]
    public int NotesUnlink(
        [Description("Source note id")] string fromId,
        [Description("Target note id")] string toId,
        [Description("Relationship verb")] string rel)
        => Translate(() =>
        {
            AuthorizeNote(fromId);
            AuthorizeNoteIfExists(toId); // don't authorize-by-existence block cleaning a dangling edge, but do gate cross-domain
            return _notes.Unlink(fromId, toId, rel);
        });

    /// <summary>Returns a note's change history (newest first), compact — no full before/after.</summary>
    [McpServerTool(Name = "notes_history", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note's append-only history (create/update/archive/restore/link/...), newest first. Compact: each entry has eventId, op, actor, ts, changedFields and diffBytes — NOT the full before/after (which can be huge). Fetch one event's detail with notes_history_event.")]
    public IReadOnlyList<NoteEvent> NotesHistory(
        [Description("Note id")] string id,
        [Description("Max events (default 50)")] int limit = 50)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? _notes.Events(id, limit) : Array.Empty<NoteEvent>();
    }

    /// <summary>Returns the full detail (before/after diff) of one history event, if it is in scope.</summary>
    [McpServerTool(Name = "notes_history_event", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get ONE history event's detail by id (op, actor, ts + the before/after diff). A diff can be huge — pass maxChars to cap it (returns diffChars + truncated) and/or fields to project it (full/before/after/changed). Use after notes_history.")]
    public NoteEventDetail? NotesHistoryEvent(
        [Description("Note id")] string id,
        [Description("Event id from notes_history")] string eventId,
        [Description("Cap the diff to this many characters (optional; sets truncated)")] int? maxChars = null,
        [Description("Diff projection: full (default), before, after, or changed")] string? fields = null)
    {
        var note = _notes.Get(id);
        return note is not null && Guard().IsAllowed(note.Domain) ? _notes.Event(id, eventId, maxChars, fields) : null;
    }

    /// <summary>Lists registered note types as type@version.</summary>
    [McpServerTool(Name = "schema_list_types", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List registered note types as type@version strings.")]
    public IReadOnlyList<string> SchemaListTypes() =>
        _schemas.All.Select(definition => $"{definition.Type}@{definition.Version}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    /// <summary>Returns the JSON Schema for a type at a specific version, or the latest.</summary>
    [McpServerTool(Name = "schema_get", ReadOnly = true, OpenWorld = false)]
    [Description("Get the JSON Schema document for a type. Omit version for the latest; pass a version to fetch that exact one. Null if unknown.")]
    public string? SchemaGet(
        [Description("Note type, e.g. backlog_item")] string type,
        [Description("Specific schema version (optional; default latest)")] int? version = null) =>
        (version.HasValue ? _schemas.Get(type, version.Value) : _schemas.GetLatest(type))?.Json;

    /// <summary>Adds or updates an agent-authored note type's JSON Schema.</summary>
    [McpServerTool(Name = "schema_upsert", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Register or update a note type's JSON Schema (draft 2020-12). The document's \"$id\" must be \"type@version\". Built-in types are read-only; a version already used by notes can't change (bump the version). Returns the stored type@version.")]
    public string SchemaUpsert([Description("A complete JSON Schema document (a JSON object; a JSON string is also accepted) with $id = type@version")] JsonElement schema)
        => Translate(() =>
        {
            var schemaJson = JsonArg(schema) ?? throw new McpException("A schema document is required.");
            var definition = _schemas.Upsert(_connectionFactory, schemaJson);
            return $"{definition.Type}@{definition.Version}";
        });

    /// <summary>Returns server/database diagnostics.</summary>
    [McpServerTool(Name = "status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Server and database diagnostics: server build version, schema version, registered schemas, note counts by type/domain/status, attachments, blob bytes + quota, pending confirmations. Check serverVersion to confirm which build prod is running.")]
    public StatusReport Status() => _diagnostics.Snapshot();

    /// <summary>Lists domains (namespaces) with their note counts.</summary>
    [McpServerTool(Name = "domains_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List domains (namespaces/workspaces) with note counts — to discover what workspaces exist.")]
    public IReadOnlyDictionary<string, long> DomainsList() => _diagnostics.Snapshot().NotesByDomain;

    /// <summary>Lists tags (facets) with their counts, most-used first.</summary>
    [McpServerTool(Name = "tags_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List tags (cross-cutting facets) with counts, most-used first — to discover the tag taxonomy.")]
    public IReadOnlyDictionary<string, long> TagsList() => _notes.TagCounts();

    /// <summary>Scans notes for data-quality issues (read-only), within the caller's scope.</summary>
    [McpServerTool(Name = "notes_lint", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Scan notes for data-quality issues (no_tags, no_dedup_key, no_title, duplicate, broken_link) and return findings (noteId/title/domain/type/rule/severity/message). 'duplicate' = another active note shares the same domain/type/title. Pass a domain to focus. Read-only — it suggests fixes, changes nothing.")]
    public IReadOnlyList<LintFinding> NotesLint(
        [Description("Domain to focus on (optional; default = all in scope)")] string? domain = null,
        [Description("Max findings (default 200, max 1000)")] int limit = 200)
        => Translate(() => new NotesLinter(_connectionFactory).Lint(domain, Guard().RestrictionForSearch(domain), limit));

    /// <summary>Lists unresolved destructive-action confirmations.</summary>
    [McpServerTool(Name = "pending_actions_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List unresolved destructive-action confirmations (so their tokens aren't lost). Resolve via notes_confirm / notes_cancel.")]
    public IReadOnlyList<PendingAction> PendingActionsList() => _confirmations.ListPending(Guard().WriteRestriction());

    // Accepts a tool argument that may be a structured object/array OR a JSON string, and returns the JSON
    // text either way (so agents can pass `{...}`/`[...]` directly instead of double-serializing). MEMP-072.
    private static string? JsonArg(JsonElement? element)
    {
        if (element is not JsonElement value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private ScopeGuard Guard() => new(_scope.Current);

    // For mutations: the note must exist and be in scope. Missing -> error (no silent no-op / dangling edge).
    private void AuthorizeNote(string id)
    {
        var note = _notes.Get(id) ?? throw new McpException($"Note '{id}' not found.");
        Guard().Authorize(note.Domain);
    }

    // Authorize a note's domain only if it still exists (e.g. unlink's target may be a removed note).
    private void AuthorizeNoteIfExists(string id)
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
        catch (ConfirmationException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (SchemaAuthoringException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (ConcurrencyException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (AssembleException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
