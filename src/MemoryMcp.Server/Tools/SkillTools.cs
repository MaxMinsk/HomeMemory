using System.ComponentModel;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

/// <summary>MCP tools for server-hosted skills — the shared guidance agents consult so they author
/// each note type the same way. Authoring is scope-authorized; reads are scope-filtered.</summary>
[McpServerToolType]
public sealed class SkillTools
{
    private readonly SkillsService _skills;
    private readonly IScopeAccessor _scope;

    /// <summary>Creates the skill tools over the service and scope accessor.</summary>
    public SkillTools(SkillsService skills, IScopeAccessor scope)
    {
        _skills = skills;
        _scope = scope;
    }

    /// <summary>Creates or updates a skill (the body holds the guidance text).</summary>
    [McpServerTool(Name = "skill_upsert", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Author or update a server-hosted skill: shared guidance for how to write a note type. Idempotent by key; bump version on meaningful change.")]
    public Skill SkillUpsert(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Stable skill key, lower-kebab, e.g. recipe-authoring")] string key,
        [Description("The skill content (markdown)")] string body,
        [Description("Human-readable title")] string? title = null,
        [Description("Note type this skill guides, e.g. recipe (optional)")] string? targetType = null,
        [Description("Version; bump on meaningful change (default 1)")] int version = 1,
        [Description("One-line description of what the skill teaches")] string? summary = null,
        [Description("Who is writing (provenance)")] string? sourceAgent = null)
        => Translate(() =>
        {
            Guard().Authorize(domain);
            return _skills.Upsert(domain, key, title, body, targetType, version, summary, sourceAgent);
        });

    /// <summary>Lists skills (metadata only) in a domain, optionally for a specific target type.</summary>
    [McpServerTool(Name = "skill_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List available skills (metadata, no body), optionally only those that guide a given note type. Call skill_get for the full text.")]
    public IReadOnlyList<Skill> SkillList(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Only skills guiding this note type (optional)")] string? targetType = null)
        => Guard().IsAllowed(domain) ? _skills.List(domain, targetType) : Array.Empty<Skill>();

    /// <summary>Returns the full skill (including body) by key, if in scope.</summary>
    [McpServerTool(Name = "skill_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a full skill (including its guidance body) by key.")]
    public Skill? SkillGet(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Skill key, e.g. recipe-authoring")] string key)
        => Guard().IsAllowed(domain) ? _skills.Get(domain, key) : null;

    private ScopeGuard Guard() => new(_scope.Current);

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
    }
}
