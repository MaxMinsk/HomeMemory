using System.Text.Json;
using MemoryMcp.Core.Notes;

namespace MemoryMcp.Core.Skills;

/// <summary>
/// Authoring and retrieval of server-hosted skills. A skill is just a note of type <c>skill</c>
/// (consistent with the single-table design), so it reuses validation, audit and soft-delete.
/// Skills are the shared conventions agents consult so they author each note type consistently.
/// </summary>
public sealed class SkillsService
{
    /// <summary>The note <c>type</c> under which skills are stored.</summary>
    public const string SkillType = "skill";

    private readonly NotesRepository _notes;

    /// <summary>Creates the service over the note repository.</summary>
    /// <param name="notes">Backing note repository.</param>
    public SkillsService(NotesRepository notes) => _notes = notes;

    /// <summary>Creates or updates a skill (idempotent by key, scoped to <paramref name="project"/> when given).
    /// A project-specific skill (e.g. a project's own backlog conventions) overrides the domain-general one
    /// with the same key; it is stored under a project-qualified dedup key so they coexist.</summary>
    /// <param name="domain">Domain to author in.</param>
    /// <param name="key">Stable skill key.</param>
    /// <param name="title">Human-readable title.</param>
    /// <param name="body">The guidance itself.</param>
    /// <param name="targetType">Note type this skill guides, if any.</param>
    /// <param name="version">Skill version; bump on a meaningful change.</param>
    /// <param name="summary">One line: what it does and when to reach for it.</param>
    /// <param name="sourceAgent">Provenance.</param>
    /// <param name="project">Project this skill overrides for, if any.</param>
    /// <param name="tagsJson">Tags as a JSON array, so a skill is findable by the same mechanism as every other
    /// note (MEMP-258). Passing null CLEARS existing tags — this is a full upsert, not a patch.</param>
    public Skill Upsert(string domain, string key, string? title, string body,
        string? targetType, int version, string? summary, string? sourceAgent, string? project = null,
        string? tagsJson = null)
    {
        var hasProject = !string.IsNullOrWhiteSpace(project);
        var payload = new Dictionary<string, object?> { ["key"] = key, ["version"] = version };
        if (!string.IsNullOrWhiteSpace(targetType))
        {
            payload["target_type"] = targetType;
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            payload["summary"] = summary;
        }

        // Project is encoded in the dedup key (skill@1 payload forbids extra fields), so a project-specific
        // skill and the domain-general one of the same key coexist as distinct notes.
        var dedupKey = hasProject ? Qualify(project!, key) : key;
        _notes.Upsert(domain, SkillType, title, body, JsonSerializer.Serialize(payload), tagsJson, dedupKey, sourceAgent ?? "skill-author", hasProject ? project : null);
        return new Skill(key, title, targetType, version, summary, body, hasProject ? project : null);
    }

    /// <summary>Lists skills in a domain (metadata only), resolved for <paramref name="project"/>: per key, the
    /// project-specific skill wins over the domain-general one; skills specific to other projects are excluded.</summary>
    public IReadOnlyList<Skill> List(string domain, string? targetType = null, string? project = null) =>
        _notes.List(domain, SkillType)
            .Select(ToSkill)
            .GroupBy(skill => skill.Key, StringComparer.Ordinal)
            .Select(group => Resolve(group, project))
            .OfType<Skill>()
            .Where(skill => targetType is null || string.Equals(skill.TargetType, targetType, StringComparison.Ordinal))
            .Select(skill => skill with { Body = null })
            .ToList();

    /// <summary>
    /// Returns the full skill (including body) for a key, resolving project, then the domain-general skill,
    /// then <c>commons</c>. Null if none of the three has it.
    /// <para><b>Why commons is part of the chain.</b> <c>memory_context</c> already offers an agent skills drawn
    /// from all three scopes, so an agent that took one of those keys and asked for it in its own domain used to
    /// get null — for a skill the same server had just recommended. The resolution rule now matches the one that
    /// produced the offer, which matters more since <c>skill_get</c> is the explicit activation point.</para>
    /// <para>The answering scope is reported rather than left to be inferred: "this came from commons" and "this
    /// is your project's override" lead to different decisions, and a caller cannot tell them apart from the body.</para>
    /// </summary>
    /// <param name="domain">Domain to resolve in.</param>
    /// <param name="key">Skill key.</param>
    /// <param name="project">Optional project whose override wins.</param>
    public Skill? Get(string domain, string key, string? project = null)
    {
        if (!string.IsNullOrWhiteSpace(project) &&
            _notes.GetByDedupKey(domain, SkillType, Qualify(project!, key)) is { } scoped)
        {
            return ToSkill(scoped) with { ResolvedFrom = "project" };
        }

        if (_notes.GetByDedupKey(domain, SkillType, key) is { } general)
        {
            return ToSkill(general) with { ResolvedFrom = "domain" };
        }

        if (string.Equals(domain, Security.ScopeGuard.CommonsDomain, StringComparison.Ordinal))
        {
            return null;
        }

        var commons = _notes.GetByDedupKey(Security.ScopeGuard.CommonsDomain, SkillType, key);
        return commons is null ? null : ToSkill(commons) with { ResolvedFrom = "commons" };
    }

    /// <summary>
    /// Builds a consolidation plan for a domain's skills (MEMP-036): redundant project overrides (a project skill
    /// whose body is identical to the domain-general one it shadows) and duplicate skills (identical bodies under
    /// different keys within the same scope). Read-only — it only proposes actions.
    /// </summary>
    /// <param name="domain">The domain to analyze.</param>
    public SkillConsolidationPlan ConsolidationPlan(string domain)
    {
        var skills = _notes.List(domain, SkillType).Select(ToSkill).Where(skill => skill.Body is not null).ToList();
        var items = new List<SkillConsolidationItem>();

        // Redundant project overrides: a project-scoped skill with the same body as the same-key general skill.
        var generalByKey = skills.Where(skill => skill.Project is null)
            .ToDictionary(skill => skill.Key, skill => skill, StringComparer.Ordinal);
        foreach (var skill in skills.Where(skill => skill.Project is not null))
        {
            if (generalByKey.TryGetValue(skill.Key, out var general) && SameBody(skill.Body, general.Body))
            {
                items.Add(new SkillConsolidationItem(skill.Key, skill.Project, "delete",
                    "Identical to the domain-general skill it overrides — the override is redundant.", skill.Key));
            }
        }

        // Duplicate bodies under different keys within the same scope (project), keep the first key, merge the rest.
        foreach (var scope in skills.GroupBy(skill => skill.Project, StringComparer.Ordinal))
        {
            foreach (var sameBody in scope.GroupBy(skill => Normalize(skill.Body), StringComparer.Ordinal))
            {
                var ordered = sameBody.OrderBy(skill => skill.Key, StringComparer.Ordinal).ToList();
                if (ordered.Count < 2)
                {
                    continue;
                }

                var canonical = ordered[0].Key;
                foreach (var duplicate in ordered.Skip(1))
                {
                    items.Add(new SkillConsolidationItem(duplicate.Key, duplicate.Project, "merge",
                        $"Identical body to skill '{canonical}' — consolidate them.", canonical));
                }
            }
        }

        return new SkillConsolidationPlan(domain, items);
    }

    private static bool SameBody(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string? body) => (body ?? string.Empty).Trim();

    private static string Qualify(string project, string key) => $"{project}::{key}";

    // From all variants of one logical key, choose the project-specific one if asked and present, else the general one.
    private static Skill? Resolve(IEnumerable<Skill> variants, string? project)
    {
        var list = variants.ToList();
        if (!string.IsNullOrWhiteSpace(project) &&
            list.FirstOrDefault(skill => string.Equals(skill.Project, project, StringComparison.Ordinal)) is { } scoped)
        {
            return scoped;
        }

        return list.FirstOrDefault(skill => skill.Project is null);
    }

    private static Skill ToSkill(Note note)
    {
        using var document = JsonDocument.Parse(note.PayloadJson ?? "{}");
        var root = document.RootElement;

        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;

        // Project (and the logical key) are recovered from the dedup key: "<project>::<key>" or just "<key>".
        var dedup = note.DedupKey ?? string.Empty;
        var separator = dedup.IndexOf("::", StringComparison.Ordinal);
        var project = separator >= 0 ? dedup[..separator] : null;
        var logicalKey = Text("key") ?? (separator >= 0 ? dedup[(separator + 2)..] : dedup);

        return new Skill(logicalKey, note.Title, Text("target_type"), version, Text("summary"), note.Body, project);
    }
}
