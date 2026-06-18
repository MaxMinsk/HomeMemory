using System.ComponentModel;
using System.Text.Json;
using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

public sealed partial class MemoryTools
{
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
        [Description("Who is writing (provenance)")] string? sourceAgent = null,
        [Description("Project within the domain (sub-axis, e.g. unity-solitaire). Filter with the DSL field `project`. Organizational only — scope stays at the domain.")] string? project = null)
    {
        try
        {
            _authz.AuthorizeWrite(domain);
            return _notes.Upsert(domain, type, title, body, JsonArg(payload), JsonArg(tags), dedupKey, sourceAgent ?? "mcp", project);
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
            _authz.AuthorizeWrite(domain);
            foreach (var link in links ?? Array.Empty<AssembleLink>())
            {
                AuthorizeNote(link.ToId);
            }

            return _notes.Assemble(domain, type, title, body, JsonArg(payload), JsonArg(tags), dedupKey, links, sourceAgent ?? "mcp");
        });

    /// <summary>Appends a schema-less free-text journal note (capture-first) and returns its envelope.</summary>
    [McpServerTool(Name = "notes_append_journal", Destructive = false, UseStructuredContent = true)]
    [Description("Append a schema-less free-text journal note (capture-first; structure it later). A title is derived from the first line if you omit one, a stable dedupKey is assigned, and the note is tagged 'unstructured' so it can be found for later structuring. Returns the created note's envelope (id, derived title, assigned dedupKey, typed tags) so you needn't follow up with a get.")]
    public NoteView? NotesAppendJournal(
        [Description("Namespace, e.g. kitchen")] string domain,
        [Description("Raw free-text content")] string text,
        [Description("Optional title (default: derived from the first line)")] string? title = null,
        [Description("Optional tags as a JSON array string ('unstructured' is always added)")] string? tags = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            _authz.AuthorizeWrite(domain);
            var id = _notes.AppendJournal(domain, text, title, tags, sourceAgent ?? "mcp");
            return _notes.GetView(id);
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
        => Translate(() => _confirmations.Confirm(token, _authz.WriteRestriction(), null));

    /// <summary>Cancels a pending destructive action so it can never execute.</summary>
    [McpServerTool(Name = "notes_cancel", Idempotent = true, UseStructuredContent = true)]
    [Description("Cancel a pending destructive action by its token so it can never execute.")]
    public ConfirmationResult NotesCancel([Description("Confirmation token")] string token)
        => Translate(() => _confirmations.Cancel(token, _authz.WriteRestriction(), null));

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

    /// <summary>Adds or updates an agent-authored note type's JSON Schema.</summary>
    [McpServerTool(Name = "schema_upsert", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Register or update a note type's JSON Schema (draft 2020-12). The document's \"$id\" must be \"type@version\". Built-in types are read-only; a version already used by notes can't change (bump the version). Returns the stored type@version.")]
    public string SchemaUpsert(
        [Description("A complete JSON Schema document (a JSON object; a JSON string is also accepted) with $id = type@version")] JsonElement schema,
        [Description("Who is authoring (recorded as provenance for audit; see schema_provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            var schemaJson = JsonArg(schema) ?? throw new McpException("A schema document is required.");
            var definition = _schemas.Upsert(_connectionFactory, schemaJson, sourceAgent);
            return $"{definition.Type}@{definition.Version}";
        });
}
