using System.ComponentModel;
using System.Text.Json;
using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

public sealed partial class MemoryTools
{
    /// <summary>Searches notes by optional full-text query plus structured filters (scope-restricted, paginated).</summary>
    [McpServerTool(Name = "notes_search", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search notes by an optional full-text query plus filters. Returns ONE page of hits (snippets + BM25 scores, never the full body) with total/hasMore — paginate with offset; do not try to fetch everything at once. Pass `sort` to order by a field instead of relevance/recency (e.g. top-N by a payload value).")]
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
        [Description("When true, each hit also includes its links (both directions), so you can render a graph without a notes_links call per row.")] bool includeLinks = false,
        [Description("Order results by a field instead of relevance/recency: \"<field> asc|desc\" (default desc). Sortable: payload.<field> (numeric fields sort numerically), title, updated_utc, created_utc. NULLs last. Or sort=\"recency\" for type-aware recency decay — freshest-relative-to-its-type first, so ephemeral types (episode/journal) fade in days while durable ones (recipe/reference/skill) stay near the top. E.g. top 10 hottest: type=pepper, sort=\"payload.spice_level desc\", limit=10.")] string? sort = null)
        => Translate(() => _notes.Search(query, domain, type, tags, status, limit, offset, _authz.ReadRestriction(domain), filter, includePayload, includeLinks, sort));

    /// <summary>Recalls a compact context block (hits + linked neighbors) for a query, scope-restricted.</summary>
    [McpServerTool(Name = "notes_recall", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Recall a prompt-ready context block for a query: the top matching notes (FTS, with payload) PLUS their one-hop linked neighbors (both directions), scope-restricted, with relation labels and source ids/revisions — a case's surrounding context in one call instead of search + many gets. Snippets only, never full bodies (use notes_read/notes_get for those).")]
    public RecallResult NotesRecall(
        [Description("Full-text query")] string query,
        [Description("Domain filter (optional)")] string? domain = null,
        [Description("Max primary hits (default 10)")] int limit = 10,
        [Description("Include one-hop linked neighbors of the hits (default true)")] bool includeLinks = true,
        [Description("Graph expansion depth (currently one hop)")] int maxHops = 1)
        => Translate(() => _notes.Recall(query, domain, limit, _authz.ReadRestriction(domain), includeLinks, maxHops));

    /// <summary>Assembles a layered context block (rules + skills + recall) for a task in a domain.</summary>
    [McpServerTool(Name = "memory_context", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Assemble a prompt-ready context block for a task in a domain, in one call: the active rules in force (memory_rule from the domain + commons, most important first), the guiding skills, and a recall of notes relevant to the query (FTS hits + one-hop neighbors). Includes an advisory-policy reminder (memory is advisory; the live user/data wins). Use at the start of a task instead of separate skill_list/search/recall calls. Null if the domain is out of scope.")]
    public ContextBlock? MemoryContext(
        [Description("The task query to recall relevant notes for")] string query,
        [Description("Namespace, e.g. development or kitchen")] string domain,
        [Description("Max recall hits (default 10)")] int limit = 10,
        [Description("Include one-hop linked neighbors of the recall hits (default true)")] bool includeLinks = true,
        [Description("Project within the domain: its skills override the domain-general ones of the same key")] string? project = null)
        => Translate(() => new ContextAssembler(_notes, _skills).Assemble(query, domain, limit, includeLinks, _authz.Scope, project));

    /// <summary>Lists the most-recently-updated (or most-used) notes in scope.</summary>
    [McpServerTool(Name = "notes_recent", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List the most-recently-updated notes (or most-used with sort=used), scope-restricted, with payload — to see what's already in memory and avoid duplicates before writing. Compact (no snippet/score). Filter by domain/type.")]
    public IReadOnlyList<SearchResult> NotesRecent(
        [Description("Domain filter (optional)")] string? domain = null,
        [Description("Type filter (optional)")] string? type = null,
        [Description("Max results (default 20)")] int limit = 20,
        [Description("Sort: 'updated' (default) or 'used' (by access count/recency)")] string? sort = null)
        => Translate(() => _notes.Recent(domain, type, limit, _authz.ReadRestriction(domain),
            string.Equals(sort, "used", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Gets a note (envelope + payload + size metadata) by id, if it is in scope.</summary>
    [McpServerTool(Name = "notes_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note (envelope + payload) by id, plus size metadata (bodyChars, attachmentCount, linkCount). By default the full body is returned. For a large note, pass includeBody=false to peek (no body) or bodyMaxChars to cap it — check `truncated`/`bodyChars` and use notes_read/notes_outline to read the rest.")]
    public NoteView? NotesGet(
        [Description("Note id")] string id,
        [Description("Include the body text (default true). Pass false to peek: envelope + payload + counts, no body.")] bool includeBody = true,
        [Description("Cap the returned body to this many characters (optional; default = full body). Sets truncated=true when it cuts.")] int? bodyMaxChars = null)
    {
        var view = _notes.GetView(id, includeBody, bodyMaxChars);
        if (view is null || !_authz.CanRead(view.Domain))
        {
            return null;
        }

        RecordUsage(id);
        return view;
    }

    /// <summary>Exact lookup of a note by its stable (domain, type, dedupKey), if in scope.</summary>
    [McpServerTool(Name = "notes_get_by_key", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note by its stable key: (domain, type, dedupKey) — the reliable way to fetch a template/skill/canonical note without remembering its id. Returns the note (envelope + payload + size metadata) or null if not found.")]
    public NoteView? NotesGetByKey(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Schema type, e.g. recipe")] string type,
        [Description("Stable dedupKey, e.g. html-template-recipe-render")] string dedupKey)
    {
        var note = _notes.GetByDedupKey(domain, type, dedupKey);
        if (note is null || !_authz.CanRead(note.Domain))
        {
            return null;
        }

        RecordUsage(note.Id);
        return _notes.GetView(note.Id);
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
        if (note is null || !_authz.CanRead(note.Domain))
        {
            return null;
        }

        RecordUsage(id);
        return _notes.ReadBody(id, offset, limitChars);
    }

    /// <summary>Returns the Markdown heading map of a note's body (for section reads), if it is in scope.</summary>
    [McpServerTool(Name = "notes_outline", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note's Markdown heading map: each heading's level, text and character offset, plus totalChars. To read one section, call notes_read(offset = that heading's offset, limitChars = next heading's offset minus it).")]
    public NoteOutline? NotesOutline([Description("Note id")] string id)
    {
        var note = _notes.Get(id);
        return note is not null && _authz.CanRead(note.Domain) ? _notes.Outline(id) : null;
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
        return note is not null && _authz.CanRead(note.Domain) ? _notes.Find(id, query, contextChars, limit) : null;
    }

    /// <summary>Rule-based advice on whether to capture a candidate note (save/update/skip/ask).</summary>
    [McpServerTool(Name = "notes_suggest_capture", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Before writing a note, ask whether you should: returns an action (save / update / skip / ask), a reason, and any existing notes behind it. Rule-based and read-only — it checks for an identical note (same content hash), a same-title/type note to update instead, or merely similar notes to review — so you avoid duplicating shared memory. Pass the same domain/type/title/body/payload/tags you intend to write.")]
    public CaptureSuggestion NotesSuggestCapture(
        [Description("Namespace the note would be written to, e.g. memory-mcp")] string domain,
        [Description("Schema type, e.g. backlog_item")] string type,
        [Description("Candidate title")] string? title = null,
        [Description("Candidate body")] string? body = null,
        [Description("Candidate payload as a JSON object (a JSON string is also accepted)")] JsonElement? payload = null,
        [Description("Candidate tags as a JSON array (a JSON array string is also accepted)")] JsonElement? tags = null)
    {
        if (!_authz.CanRead(domain))
        {
            return new CaptureSuggestion("ask", "Out of scope to check that domain for duplicates — decide manually.", Array.Empty<CaptureCandidate>());
        }

        return _notes.SuggestCapture(domain, type, title, body, JsonArg(payload), JsonArg(tags), _authz.ReadRestriction(domain));
    }

    /// <summary>Suggests notes related to a given note by shared tags (scope-restricted).</summary>
    [McpServerTool(Name = "notes_related", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find notes related to a given note by shared tags (most overlap first), scope-restricted — a linking/dedup suggestion. Returns each candidate's id/title/type/domain and the shared tags. Creating a link stays explicit (notes_link / notes_assemble).")]
    public IReadOnlyList<RelatedNote> NotesRelated(
        [Description("Note id")] string id,
        [Description("Max candidates (default 10)")] int limit = 10)
    {
        var note = _notes.Get(id);
        return note is not null && _authz.CanRead(note.Domain)
            ? _notes.Related(id, limit, _authz.ReadRestriction(null))
            : Array.Empty<RelatedNote>();
    }

    /// <summary>Lists a note's links in both directions (each resolved to the note at the other end).</summary>
    [McpServerTool(Name = "notes_links", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List a note's links (both directions): each entry has direction (out/in), rel, and the other note's id/title/type.")]
    public IReadOnlyList<LinkView> NotesLinks([Description("Note id")] string id)
    {
        var note = _notes.Get(id);
        return note is not null && _authz.CanRead(note.Domain) ? _notes.Links(id) : Array.Empty<LinkView>();
    }

    /// <summary>Traverses the link graph from a note out to N hops (nodes + edges, scope-filtered).</summary>
    [McpServerTool(Name = "notes_graph", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Traverse the link graph from a note: returns its N-hop neighborhood as nodes (id, title, type, domain, hops-from-root) + edges (fromId, toId, rel), scope-filtered. Use it to see how a note connects — dependencies, derivations, sprint membership — in one call instead of walking notes_links by hand. maxHops 1-5 (default 2); a large neighborhood is capped (truncated=true).")]
    public NoteGraph? NotesGraph(
        [Description("Root note id")] string id,
        [Description("Traversal depth, 1-5 (default 2)")] int maxHops = 2)
    {
        var note = _notes.Get(id);
        if (note is null || !_authz.CanRead(note.Domain))
        {
            return null;
        }

        RecordUsage(id);
        return _notes.Graph(id, maxHops, _authz.ReadRestriction(null));
    }

    /// <summary>Returns a note's change history (newest first), compact — no full before/after.</summary>
    [McpServerTool(Name = "notes_history", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a note's append-only history (create/update/archive/restore/link/...), newest first. Compact: each entry has eventId, op, actor, ts, changedFields and diffBytes — NOT the full before/after (which can be huge). Fetch one event's detail with notes_history_event.")]
    public IReadOnlyList<NoteEvent> NotesHistory(
        [Description("Note id")] string id,
        [Description("Max events (default 50)")] int limit = 50)
    {
        var note = _notes.Get(id);
        return note is not null && _authz.CanRead(note.Domain) ? _notes.Events(id, limit) : Array.Empty<NoteEvent>();
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
        return note is not null && _authz.CanRead(note.Domain) ? _notes.Event(id, eventId, maxChars, fields) : null;
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

    /// <summary>Lists schema authoring provenance (who registered each type@version, and when).</summary>
    [McpServerTool(Name = "schema_provenance", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List schema authoring provenance for audit: each registered type@version with its author and last-write time. Built-ins show author 'system'; agent-authored schemas show the sourceAgent passed to schema_upsert (null for legacy ones). Schema authoring itself is not domain-restricted — this is visibility, not a gate.")]
    public IReadOnlyList<SchemaProvenance> SchemaProvenanceList() => SchemaRegistry.Provenance(_connectionFactory);

    /// <summary>Returns server/database diagnostics.</summary>
    [McpServerTool(Name = "status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Server and database diagnostics: server build version, schema version, registered schemas, note counts by type/domain/status, attachments, blob bytes + quota, on-disk DB size (dbSizeBytes), pending confirmations. Check serverVersion to confirm which build prod is running.")]
    public StatusReport Status() => _diagnostics.Snapshot();

    /// <summary>Returns the runtime contract for the caller's scope (version, known types, domain access).</summary>
    [McpServerTool(Name = "memory_capabilities", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Runtime contract — call on connect to discover what this build supports instead of guessing from a stale tool list: server build version, schema version, contract version, the note types this build knows (latest schema version + whether built-in), your token's domain scope (readable/writable + commons), search backend and blob quota.")]
    public CapabilitiesReport Capabilities() => _diagnostics.Capabilities(_authz.Scope);

    /// <summary>Lists domains (namespaces) with their note counts.</summary>
    [McpServerTool(Name = "domains_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List domains (namespaces/workspaces) with note counts — to discover what workspaces exist.")]
    public IReadOnlyDictionary<string, long> DomainsList() => _diagnostics.Snapshot().NotesByDomain;

    /// <summary>Returns a compact orientation for a domain: note types, skills, and active rules.</summary>
    [McpServerTool(Name = "domain_manifest", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Domain orientation in one call: note counts by type, the domain's skills (metadata), and its active memory_rule notes (the rules in force, with payload). Pass a project to resolve project-specific skill overrides. Use when entering a domain instead of dumping everything. Null if the domain is out of scope.")]
    public DomainManifest? GetDomainManifest(
        [Description("Namespace, e.g. development or kitchen")] string domain,
        [Description("Resolve skills for this project (project-specific overrides win)")] string? project = null)
    {
        if (!_authz.CanRead(domain))
        {
            return null;
        }

        var ruleFilter = string.IsNullOrWhiteSpace(project) ? null : $"project == '{project}' OR project is null";
        var rules = _notes.Search(null, domain, "memory_rule", null, "active", 100, 0, _authz.ReadRestriction(domain), ruleFilter, includePayload: true).Items;
        return new DomainManifest(domain, _notes.CountByTypeInDomain(domain), _skills.List(domain, null, project), rules);
    }

    /// <summary>Lists tags (facets) with their counts, most-used first.</summary>
    [McpServerTool(Name = "tags_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List tags (cross-cutting facets) with counts, most-used first — to discover the tag taxonomy.")]
    public IReadOnlyDictionary<string, long> TagsList() => _notes.TagCounts();

    /// <summary>Scans notes for data-quality issues (read-only), within the caller's scope.</summary>
    [McpServerTool(Name = "notes_lint", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Scan notes for data-quality issues (no_tags, no_dedup_key, no_title, duplicate, broken_link, stale_unstructured, stale_unverified, oversized_no_summary, possible_secret) and return findings (noteId/title/domain/type/rule/severity/message). 'duplicate' = another active note shares the same domain/type/title; 'stale_unstructured' = tagged 'unstructured' and untouched for 30+ days; 'stale_unverified' = a note with payload.stale_after_days not re-verified within that window (e.g. an aging memory_rule); 'oversized_no_summary' = a large body with no heading/summary; 'possible_secret' = body/payload heuristically looks like a credential (value never echoed). Pass a domain to focus. Read-only — it suggests fixes, changes nothing.")]
    public IReadOnlyList<LintFinding> NotesLint(
        [Description("Domain to focus on (optional; default = all in scope)")] string? domain = null,
        [Description("Max findings (default 200, max 1000)")] int limit = 200)
        => Translate(() => new NotesLinter(_connectionFactory).Lint(domain, _authz.ReadRestriction(domain), limit));

    /// <summary>Lists unresolved destructive-action confirmations.</summary>
    [McpServerTool(Name = "pending_actions_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List unresolved destructive-action confirmations (so their tokens aren't lost). Resolve via notes_confirm / notes_cancel.")]
    public IReadOnlyList<PendingAction> PendingActionsList() => _confirmations.ListPending(_authz.WriteRestriction());
}
