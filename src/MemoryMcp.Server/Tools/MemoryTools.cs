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

    /// <summary>Creates the tool set over the repository, registry, diagnostics, request authorizer, skills, confirmations and database.</summary>
    public MemoryTools(NotesRepository notes, SchemaRegistry schemas, DiagnosticsService diagnostics, RequestAuthorizer authz,
        SkillsService skills, ConfirmationService confirmations, ISqliteConnectionFactory connectionFactory)
    {
        _notes = notes;
        _schemas = schemas;
        _diagnostics = diagnostics;
        _authz = authz;
        _skills = skills;
        _confirmations = confirmations;
        _connectionFactory = connectionFactory;
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
