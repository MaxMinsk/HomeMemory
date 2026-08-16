using System.Text.Json;
using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Server.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

/// <summary>The Memory MCP tool surface. Each method is a model-callable tool over the note store,
/// authorized against the caller's domain scope. Read tools advertise structured output; expected
/// failures surface to the model as <see cref="McpException"/> messages.</summary>
[McpServerToolType]
public sealed partial class MemoryTools
{
    private readonly NotesRepository _notes;
    private readonly SchemaRegistry _schemas;
    private readonly DiagnosticsService _diagnostics;
    private readonly RequestAuthorizer _authz;
    private readonly SkillsService _skills;
    private readonly ConfirmationService _confirmations;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ReadActivityTracker _reads;
    private readonly AdoptionOptions _adoption;

    /// <summary>Creates the tool set over the repository, registry, diagnostics, request authorizer, skills, confirmations, database, read-activity tracker and adoption options.</summary>
    public MemoryTools(NotesRepository notes, SchemaRegistry schemas, DiagnosticsService diagnostics, RequestAuthorizer authz,
        SkillsService skills, ConfirmationService confirmations, ISqliteConnectionFactory connectionFactory,
        ReadActivityTracker reads, AdoptionOptions adoption)
    {
        _notes = notes;
        _schemas = schemas;
        _diagnostics = diagnostics;
        _authz = authz;
        _skills = skills;
        _confirmations = confirmations;
        _connectionFactory = connectionFactory;
        _reads = reads;
        _adoption = adoption;
    }

    // Post-write adoption hints (MEMP-204/205): for a newly created note, suggest a few related notes to link; and
    // nudge an identified agent that wrote without a recent recall. Both advisory; both honour the adoption toggle.
    private (IReadOnlyList<RelatedNote>? Related, string? Nudge) AdoptionHints(string id, bool created, string domain, string? sourceAgent)
    {
        if (!_adoption.Enabled)
        {
            return (null, null);
        }

        // Candidates are computed for updates too (MEMP-244): rewriting a note is a common way to end up with two
        // competing statements, since the older one sits untouched beside the new wording. Only the CREATE path
        // returns the full related list, as documented — an update just gets told if it is restating something.
        var hits = _notes.Related(id, 3, _authz.ReadRestriction(domain));
        var related = created && hits.Count > 0 ? hits : null;

        return (related, SupersedeNudge(hits) ?? RecallNudge(sourceAgent));
    }

    // When a new note competes with an existing one — same type, same project, overlapping content — say so and
    // point at notes_supersede (MEMP-240). Two parallel statements of the same fact leave the reader no way to tell
    // which one is current, and the older one keeps being recalled long after it stopped being true. Takes
    // precedence over the recall nudge: the agent has already been handed the note it would have recalled.
    private static string? SupersedeNudge(IReadOnlyList<RelatedNote>? related)
    {
        var competing = related?
            .Where(note => note.Reasons.Contains(NotesReader.SupersedeCandidateReason, StringComparer.Ordinal))
            .ToArray();
        if (competing is not { Length: > 0 })
        {
            return null;
        }

        var names = string.Join(", ", competing.Select(note => $"'{note.Title ?? note.Id}' ({note.Id})"));
        return $"This note may restate what {names} already says. If it replaces one, call notes_supersede rather " +
            "than leaving both — a superseded note drops out of recall, a parallel one competes with it.";
    }

    // The nudge fires only for an agent that identifies itself (a real sourceAgent, not the anonymous "mcp"
    // default) and has no recall recorded this session — so the default/anonymous caller is never nagged.
    private string? RecallNudge(string? sourceAgent)
    {
        if (string.IsNullOrWhiteSpace(sourceAgent) || string.Equals(sourceAgent, "mcp", StringComparison.Ordinal))
        {
            return null;
        }

        return _reads.HasRecentRead(sourceAgent)
            ? null
            : $"No recall/search recorded for '{sourceAgent}' before this write — consider memory_context/notes_recall first to build on existing notes and avoid duplicates.";
    }

    private string SkillHint(string domain, string type)
    {
        var keys = _skills.List(domain, type).Select(skill => skill.Key).ToArray();
        return keys.Length == 0 ? string.Empty : $" Guidance available — call skill_get for: {string.Join(", ", keys)}.";
    }

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

    // Best-effort access signal for recency/most-used discovery; never let it break a read (MEMP-116).
    private void RecordUsage(string id)
    {
        try
        {
            new UsageStore(_connectionFactory).Record(id);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // usage tracking is non-critical
        }
    }

    // Records a recall/search by an identified agent: the in-process nudge tracker (MEMP-204) plus the persistent
    // per-agent read counter behind the adoption report (MEMP-207). Both no-op for a null/blank agent; the
    // persistent write is best-effort and must never break a read.
    private void RecordAgentRead(string? sourceAgent)
    {
        _reads.RecordRead(sourceAgent);
        try
        {
            new AgentReadStore(_connectionFactory).Record(sourceAgent);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // read-count tracking is non-critical
        }
    }

    // For mutations: the note must exist and be in scope. Missing -> error (no silent no-op / dangling edge).
    private void AuthorizeNote(string id)
    {
        var note = _notes.Get(id) ?? throw new McpException($"Note '{id}' not found.");
        _authz.AuthorizeWrite(note.Domain);
    }

    // Authorize a note's domain only if it still exists (e.g. unlink's target may be a removed note).
    private void AuthorizeNoteIfExists(string id)
    {
        var note = _notes.Get(id);
        if (note is not null)
        {
            _authz.AuthorizeWrite(note.Domain);
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
