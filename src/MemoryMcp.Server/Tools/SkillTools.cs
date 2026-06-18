using System.ComponentModel;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Server.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Tools;

/// <summary>MCP tools for server-hosted skills — the shared guidance agents consult so they author
/// each note type the same way. Authoring is scope-authorized; reads are scope-filtered.</summary>
[McpServerToolType]
public sealed class SkillTools
{
    private readonly SkillsService _skills;
    private readonly RequestAuthorizer _authz;

    /// <summary>Creates the skill tools over the service and the request authorizer.</summary>
    public SkillTools(SkillsService skills, RequestAuthorizer authz)
    {
        _skills = skills;
        _authz = authz;
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
        [Description("Who is writing (provenance)")] string? sourceAgent = null,
        [Description("Project this skill is specific to (overrides the domain-general one of the same key); omit for a general skill")] string? project = null)
        => Translate(() =>
        {
            _authz.AuthorizeWrite(domain);
            return _skills.Upsert(domain, key, title, body, targetType, version, summary, sourceAgent, project);
        });

    /// <summary>Lists skills (metadata only) in a domain, optionally for a specific target type.</summary>
    [McpServerTool(Name = "skill_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List available skills (metadata, no body), optionally only those that guide a given note type. Pass a project to get its overrides (a project-specific skill replaces the domain-general one of the same key). Call skill_get for the full text.")]
    public IReadOnlyList<Skill> SkillList(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Only skills guiding this note type (optional)")] string? targetType = null,
        [Description("Resolve for this project (project-specific skills override domain-general ones)")] string? project = null)
        => _authz.CanRead(domain) ? _skills.List(domain, targetType, project) : Array.Empty<Skill>();

    /// <summary>Returns the full skill (including body) by key, if in scope.</summary>
    [McpServerTool(Name = "skill_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a full skill (including its guidance body) by key. Pass a project to prefer its project-specific override, falling back to the domain-general skill.")]
    public Skill? SkillGet(
        [Description("Namespace, e.g. memory-mcp")] string domain,
        [Description("Skill key, e.g. recipe-authoring")] string key,
        [Description("Prefer this project's override of the key (falls back to the general skill)")] string? project = null)
        => _authz.CanRead(domain) ? _skills.Get(domain, key, project) : null;

    /// <summary>Proposes how to consolidate a domain's redundant/duplicate skills (read-only), if in scope.</summary>
    [McpServerTool(Name = "skill_consolidate_plan", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read-only consolidation plan for a domain's skills: flags redundant project overrides (a project skill identical to the domain-general one it shadows) and duplicate skills (identical bodies under different keys), each with a suggested action (delete / merge / noop) and the related key. Nothing is changed — apply the suggestions yourself via skill_upsert. Empty when nothing is redundant.")]
    public SkillConsolidationPlan? SkillConsolidatePlan(
        [Description("Namespace, e.g. memory-mcp")] string domain)
        => _authz.CanRead(domain) ? _skills.ConsolidationPlan(domain) : null;

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
