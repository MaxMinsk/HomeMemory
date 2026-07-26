using System.ComponentModel;
using System.Text;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Skills;
using MemoryMcp.Server.Security;
using ModelContextProtocol.Server;

namespace MemoryMcp.Server.Prompts;

/// <summary>
/// MCP capability prompts (MEMP-211) that make memory adoption a single action in a prompt-aware harness
/// (e.g. Claude Code: <c>/mcp__memory__start-task</c>, <c>/mcp__memory__end-task</c>). <c>start-task</c>
/// loads the layered context (rules + skills + relevant notes) up front so the agent recalls before it acts;
/// <c>end-task</c> returns a consolidation checklist so durable facts get written back. Scope-restricted via
/// the same <see cref="RequestAuthorizer"/> the tools use.
/// </summary>
[McpServerPromptType]
public sealed class MemoryPrompts
{
    private const int RecallLimit = 10;
    private const int SnippetChars = 160;

    private readonly NotesRepository _notes;
    private readonly SkillsService _skills;
    private readonly RequestAuthorizer _authz;

    /// <summary>Creates the prompt set over the note store, skills service and authorizer.</summary>
    public MemoryPrompts(NotesRepository notes, SkillsService skills, RequestAuthorizer authz)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _authz = authz ?? throw new ArgumentNullException(nameof(authz));
    }

    /// <summary>Assembles the layered memory context for a task as a ready-to-use prompt message.</summary>
    [McpServerPrompt(Name = "start-task")]
    [Description("Load memory BEFORE you start: assembles the rules in force, the skills that guide this workspace, and the notes relevant to your task in one message. OMIT `domain` to span every domain you can read; pass domain/project to focus. Run this first so you recall before you act.")]
    public string StartTask(
        [Description("What you're about to work on (used to recall the most relevant notes). Optional.")] string? task = null,
        [Description("Domain/workspace, e.g. development or kitchen. OMIT to span all your domains.")] string? domain = null,
        [Description("Project within the domain, e.g. memory-mcp.")] string? project = null)
    {
        var query = string.IsNullOrWhiteSpace(task) ? "active work, current state, decisions, rules" : task!.Trim();
        var block = new ContextAssembler(_notes, _skills).Assemble(query, domain, RecallLimit, includeLinks: true, _authz.Scope, project);
        return block is null
            ? $"Domain '{domain}' is out of your read scope. Call domains_list to see what you can read, or omit domain to span all your domains."
            : RenderStartTask(query, project, block);
    }

    /// <summary>Returns a self-contained scaffold (templates + hooks + config) to onboard a new project.</summary>
    [McpServerPrompt(Name = "onboard-project")]
    [Description("Scaffold a NEW project to use Memory MCP: returns the exact files to create - CLAUDE.md/AGENTS.md (recall-first guidance), the Claude Code hooks (SessionStart recall + Stop nudge), the settings.json hooks block, and .claude/memory.json - with your domain/project filled in. Run this once in a fresh project, then create the files it lists and restart the session.")]
    public string OnboardProject(
        [Description("Domain/workspace this project's notes live in, e.g. development. Optional but recommended.")] string? domain = null,
        [Description("Project sub-axis within the domain, e.g. my-project. Optional.")] string? project = null)
        => RenderOnboarding(domain, project);

    /// <summary>Returns an end-of-task consolidation checklist (plus the workspace's skills).</summary>
    [McpServerPrompt(Name = "end-task")]
    [Description("Consolidate memory BEFORE you finish: a checklist to save durable facts/decisions/state, link new notes, refine skills, and flag stale notes — plus the skills that guide this workspace. Run this at the end of a task so what you learned survives.")]
    public string EndTask(
        [Description("Domain/workspace you worked in, e.g. development. Optional.")] string? domain = null,
        [Description("Project within the domain, e.g. memory-mcp. Optional.")] string? project = null)
        => RenderEndTask(domain, project);

    private static string RenderStartTask(string query, string? project, ContextBlock block)
    {
        var scope = block.Domain == "*"
            ? "all your authorized domains"
            : $"domain '{block.Domain}'" + (string.IsNullOrWhiteSpace(project) ? string.Empty : $", project '{project}'");

        var sb = new StringBuilder();
        sb.Append("# Memory context for: ").Append(query).Append('\n');
        sb.Append("Scope: ").Append(scope).Append("\n\n");
        sb.Append(block.Policy).Append("\n\n");
        AppendRules(sb, block.Rules);
        AppendSkills(sb, "Skills available (skill_get)", block.Skills);
        AppendRecall(sb, block.Recall);
        AppendWorkingGuidance(sb);
        if (block.Warnings.Count > 0)
        {
            sb.Append("\n## Notes\n");
            foreach (var warning in block.Warnings)
            {
                sb.Append("- ").Append(warning).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static void AppendWorkingGuidance(StringBuilder sb)
    {
        sb.Append("## How to work with memory\n");
        sb.Append("- The above is advisory: the live user and current data win over stored notes.\n");
        sb.Append("- As you work, SAVE durable facts/decisions/state (notes_upsert; check notes_suggest_capture first to avoid duplicates; prefer notes_patch to edit an existing note; never secrets).\n");
        sb.Append("- Link new notes into the graph (notes_link); to fix another note, write a memory_evolution_suggestion instead of silently editing it.\n");
        sb.Append("- When you finish, run the end-task prompt to consolidate.\n");
    }

    private static void AppendRules(StringBuilder sb, IReadOnlyList<SearchResult> rules)
    {
        if (rules.Count == 0)
        {
            return;
        }

        sb.Append("## Rules in force\n");
        foreach (var rule in rules)
        {
            sb.Append("- ").Append(rule.Title ?? rule.DedupKey ?? rule.Id).Append('\n');
        }

        sb.Append('\n');
    }

    private static void AppendSkills(StringBuilder sb, string heading, IReadOnlyList<Skill> skills)
    {
        if (skills.Count == 0)
        {
            return;
        }

        sb.Append("## ").Append(heading).Append('\n');
        foreach (var skill in skills)
        {
            sb.Append("- **").Append(skill.Key).Append("**: ").Append(skill.Summary ?? skill.Title ?? string.Empty).Append('\n');
        }

        sb.Append('\n');
    }

    private static void AppendRecall(StringBuilder sb, RecallResult recall)
    {
        if (recall.Hits.Count == 0)
        {
            sb.Append("## Relevant notes\n(none found — you may be starting fresh here)\n\n");
            return;
        }

        sb.Append("## Relevant notes (recall)\n");
        foreach (var hit in recall.Hits)
        {
            var snippet = (hit.Snippet ?? string.Empty).Replace('\n', ' ');
            if (snippet.Length > SnippetChars)
            {
                snippet = snippet[..SnippetChars];
            }

            sb.Append("- **").Append(hit.Title ?? "(untitled)").Append("** [")
              .Append(hit.Type).Append(", ").Append(hit.Domain).Append(", id ").Append(hit.Id).Append(']');
            if (snippet.Length > 0)
            {
                sb.Append(" - ").Append(snippet);
            }

            sb.Append('\n');
        }

        sb.Append('\n');
    }

    private string RenderOnboarding(string? domain, string? project)
    {
        var dom = string.IsNullOrWhiteSpace(domain) ? "<YOUR_DOMAIN>" : domain!.Trim();
        var proj = string.IsNullOrWhiteSpace(project) ? "<YOUR_PROJECT>" : project!.Trim();
        string Fill(string s) => s.Replace("<YOUR_DOMAIN>", dom, StringComparison.Ordinal).Replace("<YOUR_PROJECT>", proj, StringComparison.Ordinal);

        var memoryJson = $"{{\n  \"domain\": \"{dom}\",\n  \"project\": \"{proj}\",\n  \"query\": \"project state, active backlog, decisions, rules\"\n}}";

        var sb = new StringBuilder();
        sb.Append("# Onboard this project onto Memory MCP\n\n");
        sb.Append("Create the files below in THIS project (values filled for domain='").Append(dom).Append("', project='").Append(proj)
          .Append("'), then restart the session so the hooks load. Skip any file you don't want.\n\n");
        AppendFileSection(sb, "1. `AGENTS.md` (project root)", "markdown", Fill(StripTemplateHeader(LoadKitFile("onboard-kit-agents-md", "onboard.AGENTS.md"))));
        AppendFileSection(sb, "2. `CLAUDE.md` (project root)", "markdown", Fill(StripTemplateHeader(LoadKitFile("onboard-kit-claude-md", "onboard.CLAUDE.md"))));
        AppendFileSection(sb, "3. `.claude/memory.json`", "json", memoryJson);
        AppendFileSection(sb, "4. `.claude/hooks/memory_session_start.py`", "python", LoadKitFile("onboard-kit-hook-session-start", "onboard.memory_session_start.py"));
        AppendFileSection(sb, "5. `.claude/hooks/memory_stop_reminder.py`", "python", LoadKitFile("onboard-kit-hook-stop-reminder", "onboard.memory_stop_reminder.py"));
        AppendFileSection(sb, "6. Merge into `.claude/settings.json`", "json", LoadKitFile("onboard-kit-settings-json", "onboard.settings.json"));
        sb.Append("## After creating the files\n");
        sb.Append("- Make the hooks executable: `chmod +x .claude/hooks/*.py`\n");
        sb.Append("- Ensure this project has the `memory` MCP server configured (the hooks read its URL + token from ~/.claude.json), or set MEMORY_MCP_URL / MEMORY_MCP_TOKEN.\n");
        sb.Append("- Restart the session; you should see \"Loading Memory MCP context...\" and a memory summary.\n");
        sb.Append("- On demand: /mcp__memory__start-task to recall, /mcp__memory__end-task to consolidate.\n");
        if (dom == "<YOUR_DOMAIN>")
        {
            sb.Append("\n> No domain/project given: replace <YOUR_DOMAIN>/<YOUR_PROJECT> above (call domains_list to pick), or leave domain out for cross-domain recall.\n");
        }

        return sb.ToString();
    }

    // Wraps one file's content in a fenced block. Four-backtick fences so the markdown templates' own
    // triple-backtick code blocks survive intact.
    private static void AppendFileSection(StringBuilder sb, string heading, string lang, string content)
    {
        sb.Append("## ").Append(heading).Append('\n');
        sb.Append("````").Append(lang).Append('\n');
        sb.Append(content.TrimEnd('\r', '\n')).Append('\n');
        sb.Append("````\n\n");
    }

    // Drops the leading "<!-- TEMPLATE ... -->" header so an onboarded project gets a clean file.
    private static string StripTemplateHeader(string content)
    {
        if (!content.StartsWith("<!--", StringComparison.Ordinal))
        {
            return content;
        }

        var end = content.IndexOf("-->", StringComparison.Ordinal);
        return end < 0 ? content : content[(end + 3)..].TrimStart('\r', '\n');
    }

    // Loads one kit file, preferring a live-editable copy in commons memory
    // (reference note, dedupKey=commonsKey) so the owner can tweak the templates via notes_patch without a
    // release; falls back to the embedded resource so onboarding always works even before commons is seeded.
    private string LoadKitFile(string commonsKey, string embeddedName)
    {
        var note = _notes.GetByDedupKey("commons", "reference", commonsKey);
        return note is not null && !string.IsNullOrWhiteSpace(note.Body) ? note.Body! : ReadResource(embeddedName);
    }

    // Reads an embedded onboarding-kit resource (the canonical files live in ../../integrations/).
    private static string ReadResource(string logicalName)
    {
        var assembly = typeof(MemoryPrompts).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(logicalName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string RenderEndTask(string? domain, string? project)
    {
        var where = string.IsNullOrWhiteSpace(domain)
            ? "your workspace"
            : $"domain '{domain}'" + (string.IsNullOrWhiteSpace(project) ? string.Empty : $", project '{project}'");
        var recallHint = string.IsNullOrWhiteSpace(project) ? "memory_context(query, domain)" : "memory_context(query, domain, project)";

        var sb = new StringBuilder();
        sb.Append("# End-of-task memory consolidation - ").Append(where).Append("\n\n");
        sb.Append("Before you finish, capture what future-you and other agents will need. Recall first to avoid duplicates: ").Append(recallHint).Append(".\n\n");
        sb.Append("1. New durable facts/decisions/preferences? -> notes_upsert (check notes_suggest_capture first; never secrets; ask before sensitive personal info).\n");
        sb.Append("2. Project state or status changed? -> update the project_state note with notes_patch (keep its `updated` field fresh).\n");
        sb.Append("3. Backlog moved (item done / new / re-scoped)? -> patch the backlog_item(s).\n");
        sb.Append("4. New notes that stand alone? -> notes_link them into the graph so they're findable.\n");
        sb.Append("5. Learned a repeatable procedure or convention? -> capture or refine a skill (skill_upsert).\n");
        sb.Append("6. Found a stale or wrong note? -> write a memory_evolution_suggestion (target_id + proposed_patch + rationale); do NOT silently edit another agent's note.\n");

        if (!string.IsNullOrWhiteSpace(domain) && _authz.CanRead(domain))
        {
            AppendSkills(sb.Append('\n'), $"Skills that guide {domain} (skill_get)", _skills.List(domain, null, project).ToList());
        }

        sb.Append("\nMemory is advisory, but durable knowledge only persists if you write it. Mention briefly what you saved.\n");
        return sb.ToString();
    }
}
