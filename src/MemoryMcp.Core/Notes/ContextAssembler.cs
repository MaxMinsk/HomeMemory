using System.Globalization;
using System.Text.Json;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;

namespace MemoryMcp.Core.Notes;

/// <summary>Assembles a <see cref="ContextBlock"/> for a task: the domain's (and commons') active rules and
/// skills plus a query recall, in one call (MEMP-137). Scope-checked; rules ranked always_apply then priority.</summary>
public sealed class ContextAssembler
{
    private const int MaxRules = 20;
    // MEMP-214: a bare memory_context self-limits its recall to this snippet-char budget unless the caller sets one.
    private const int DefaultBudgetChars = 6000;
    private const string AdvisoryPolicy =
        "Memory is advisory: the current user's instructions and live data take precedence over stored notes. Treat rules as defaults, not overrides.";

    private readonly NotesRepository _notes;
    private readonly SkillsService _skills;
    private readonly TimeProvider _clock;

    /// <summary>Creates the assembler over the note store, skills service and clock (for stale-rule warnings).</summary>
    public ContextAssembler(NotesRepository notes, SkillsService skills, TimeProvider? clock = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Builds the context block, or null if the domain is out of read scope. When <paramref name="domain"/>
    /// is omitted, assembles a cross-domain overview across every domain the caller may read (MEMP-213).</summary>
    /// <param name="query">The task query to recall notes for.</param>
    /// <param name="domain">The domain to assemble for; null/empty = a cross-domain overview across all authorized domains.</param>
    /// <param name="limit">Max recall hits.</param>
    /// <param name="includeLinks">Include one-hop neighbors in the recall.</param>
    /// <param name="scope">The caller's request scope.</param>
    /// <param name="project">Optional project: its skills/rules override the domain-general ones, and its notes are boosted in recall (MEMP-209).</param>
    /// <param name="budgetChars">When set, pack recall hits to this snippet-char budget instead of a fixed count (MEMP-176).</param>
    /// <param name="projectOnly">When true with a <paramref name="project"/>, hard-restrict the recall to that project (MEMP-209).</param>
    /// <param name="includePayload">When false (the default, MEMP-214), recall hits carry snippet + identity only (no full payload) so the block stays lean.</param>
    public ContextBlock? Assemble(string query, string? domain, int limit, bool includeLinks, RequestScope scope, string? project = null, int? budgetChars = null, bool projectOnly = false, bool includePayload = false)
    {
        var guard = new ScopeGuard(scope);
        var warnings = new List<string>();
        var effectiveBudget = budgetChars ?? DefaultBudgetChars; // MEMP-214: self-limit a bare call

        // MEMP-213: no domain => a cross-domain overview across everything the caller may read, instead of forcing a guess.
        if (string.IsNullOrWhiteSpace(domain))
        {
            return AssembleOverview(query, limit, includeLinks, guard, project, effectiveBudget, projectOnly, includePayload);
        }

        (domain, project) = ResolveScope(guard, domain, project, warnings);

        if (!guard.IsAllowed(domain))
        {
            return null;
        }

        // The domain plus the shared commons baseline (deduped when the domain IS commons).
        var domains = string.Equals(domain, ScopeGuard.CommonsDomain, StringComparison.Ordinal)
            ? new[] { domain }
            : new[] { domain, ScopeGuard.CommonsDomain };

        // When a project is given, load that project's rules plus the domain-general ones (project IS NULL).
        var ruleFilter = string.IsNullOrWhiteSpace(project) ? null : $"project == '{project}' OR project is null";

        var rules = new List<SearchResult>();
        var skills = new List<Skill>();
        foreach (var scoped in domains)
        {
            var restrict = guard.RestrictionForSearch(scoped);
            rules.AddRange(_notes.Search(null, scoped, "memory_rule", null, "active", 50, 0, restrict, ruleFilter, includePayload: true).Items
                .Where(rule => !string.Equals(Field(rule.PayloadJson, "status"), "deprecated", StringComparison.Ordinal)));
            // Project overrides apply within the task's own domain; commons stays general.
            skills.AddRange(_skills.List(scoped, null, string.Equals(scoped, domain, StringComparison.Ordinal) ? project : null));
        }

        // Dedupe skills by key when merging the domain with commons (the domain's wins, listed first).
        var dedupedSkills = skills.GroupBy(skill => skill.Key, StringComparer.Ordinal).Select(group => group.First()).ToList();

        var ranked = rules.OrderByDescending(AlwaysApply).ThenByDescending(Priority).ToList();
        if (ranked.Count > MaxRules)
        {
            warnings.Add($"Showing top {MaxRules} of {ranked.Count} rules by priority.");
            ranked = ranked.GetRange(0, MaxRules);
        }

        var now = _clock.GetUtcNow();
        var stale = ranked.Count(rule => IsStaleRule(rule.PayloadJson, now));
        if (stale > 0)
        {
            warnings.Add($"{stale} included rule(s) may be outdated (unverified past their window) — verify or deprecate them.");
        }

        // Nudge the agent to refresh an aging project_state / stale-marked note at the end of the task (MEMP-206).
        warnings.AddRange(StaleStateWarnings(domain, project, now, guard.RestrictionForSearch(domain)));

        var recall = _notes.Recall(query, domain, limit, guard.RestrictionForSearch(domain), includeLinks, 1, effectiveBudget, false, project, projectOnly, includePayload: includePayload);
        return new ContextBlock(domain, LeanRules(ranked), dedupedSkills, recall, AdvisoryPolicy, warnings);
    }

    // Sentinel Domain value on a cross-domain overview block (no single domain was requested).
    private const string AllDomains = "*";

    // MEMP-213: a cross-domain overview when no domain is given — the rules in force across every domain the caller
    // may read (restricted to the caller's scope), the shared commons skills, and a domain-diverse recall so one big
    // domain doesn't drown the rest. A project= still boosts across domains.
    private ContextBlock? AssembleOverview(string query, int limit, bool includeLinks, ScopeGuard guard, string? project, int? budgetChars, bool projectOnly, bool includePayload)
    {
        var restrict = guard.RestrictionForSearch(null); // every authorized domain (null = unrestricted)
        var ruleFilter = string.IsNullOrWhiteSpace(project) ? null : $"project == '{project}' OR project is null";

        var rules = _notes.Search(null, null, "memory_rule", null, "active", 50, 0, restrict, ruleFilter, includePayload: true).Items
            .Where(rule => !string.Equals(Field(rule.PayloadJson, "status"), "deprecated", StringComparison.Ordinal))
            .ToList();
        var skills = _skills.List(ScopeGuard.CommonsDomain, null, null).ToList();

        var warnings = new List<string>
        {
            "No domain specified: showing a cross-domain overview across all your authorized domains (commons rules/skills + a domain-diverse recall). Pass domain= to focus on one domain's full rules and skills.",
        };

        var ranked = rules.OrderByDescending(AlwaysApply).ThenByDescending(Priority).ToList();
        if (ranked.Count > MaxRules)
        {
            warnings.Add($"Showing top {MaxRules} of {ranked.Count} rules across your domains by priority.");
            ranked = ranked.GetRange(0, MaxRules);
        }

        var stale = ranked.Count(rule => IsStaleRule(rule.PayloadJson, _clock.GetUtcNow()));
        if (stale > 0)
        {
            warnings.Add($"{stale} included rule(s) may be outdated (unverified past their window) — verify or deprecate them.");
        }

        var recall = _notes.Recall(query, null, limit, restrict, includeLinks, 1, budgetChars, false, project, projectOnly, diverseByDomain: true, includePayload: includePayload);
        return new ContextBlock(AllDomains, LeanRules(ranked), skills, recall, AdvisoryPolicy, warnings);
    }

    // MEMP-212: a caller that passed a project name where a domain is expected (e.g. domain='unity-solitaire')
    // is auto-resolved to the real domain + project with a corrective warning, instead of an empty block. Skipped
    // when a project is already given (the caller clearly knows the axes apart).
    private (string Domain, string? Project) ResolveScope(ScopeGuard guard, string domain, string? project, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            return (domain, project);
        }

        var resolved = _notes.ResolveProjectAsDomain(domain, guard.RestrictionForSearch(null));
        if (resolved is null)
        {
            return (domain, project);
        }

        warnings.Add(
            $"'{domain}' is a project, not a domain. Resolved to domain='{resolved.Domain}', project='{domain}' " +
            $"({resolved.NoteCount} notes). Next time call memory_context(domain='{resolved.Domain}', project='{domain}').");
        return (resolved.Domain, domain);
    }

    private static bool AlwaysApply(SearchResult rule) =>
        Element(rule.PayloadJson, "always_apply") is { ValueKind: JsonValueKind.True };

    private static int Priority(SearchResult rule) =>
        Element(rule.PayloadJson, "priority") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var p) ? p : 0;

    private static string? Field(string? json, string name) =>
        Element(json, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    // MEMP-214: the context view keeps only the small, decision-relevant rule fields and drops verbose arrays
    // (trigger_phrases, source_refs, ...) and tags, so a rule set doesn't bloat the block. Staleness is computed
    // from the full payload BEFORE this projection, so nothing is lost there.
    private static readonly string[] RuleKeepFields = { "description", "priority", "always_apply", "scope" };

    private static IReadOnlyList<SearchResult> LeanRules(IReadOnlyList<SearchResult> rules) =>
        rules.Select(rule => rule with { PayloadJson = LeanRulePayload(rule.PayloadJson), TagsJson = null }).ToList();

    private static string? LeanRulePayload(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return payloadJson;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return payloadJson;
            }

            var kept = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var name in RuleKeepFields)
            {
                if (document.RootElement.TryGetProperty(name, out var value))
                {
                    kept[name] = value.Clone();
                }
            }

            return JsonSerializer.Serialize(kept);
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }

    // A rule "opted into" verification (stale_after_days) is stale if never verified, or verified longer ago than the window.
    private static bool IsStaleRule(string? payloadJson, DateTimeOffset now)
    {
        if (Element(payloadJson, "stale_after_days") is not { ValueKind: JsonValueKind.Number } sad
            || !sad.TryGetInt32(out var days) || days <= 0)
        {
            return false;
        }

        if (Field(payloadJson, "last_verified_at") is not { } verified)
        {
            return true; // opted in but never verified
        }

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return !DateTimeOffset.TryParse(verified, CultureInfo.InvariantCulture, styles, out var since)
            || (now - since).TotalDays > days;
    }

    private const int MaxStateWarnings = 3;
    // A project_state has no stale_after_days field of its own, so it ages against a default window (MEMP-206).
    private const int DefaultProjectStateStaleDays = 14;

    // State notes past their staleness window, so memory_context can nudge the agent to refresh them at the end of
    // the task (MEMP-206): a project_state older than the default window, or any note that opted in via
    // payload.stale_after_days. memory_rule is excluded — aging rules are already covered by the warning above.
    private IReadOnlyList<string> StaleStateWarnings(string domain, string? project, DateTimeOffset now, IReadOnlyCollection<string>? restrict)
    {
        var filter = "(type == 'project_state' OR payload.stale_after_days is not null) AND type != 'memory_rule'";
        if (!string.IsNullOrWhiteSpace(project))
        {
            filter += $" AND (project == '{project}' OR project is null)";
        }

        var candidates = _notes.Search(null, domain, null, null, "active", 50, 0, restrict, filter, includePayload: true).Items;
        var warnings = new List<string>();
        foreach (var note in candidates)
        {
            var stale = StaleAge(note.Type, note.PayloadJson, note.UpdatedUtc, now);
            if (stale is null)
            {
                continue;
            }

            var (days, ageDays) = stale.Value;
            var label = string.IsNullOrWhiteSpace(note.Title) ? note.Id : note.Title!;
            warnings.Add($"{note.Type} '{label}' ({note.Id}) may be stale: last updated {ageDays}d ago (window {days}d) — refresh it at the end of the task.");
            if (warnings.Count >= MaxStateWarnings)
            {
                break;
            }
        }

        return warnings;
    }

    // The (window, age) in days when a note is past its staleness window; null otherwise. Window = explicit
    // payload.stale_after_days, else the default for a project_state. Reference time = payload.updated (a
    // project_state's own timestamp), else payload.last_verified_at, else the note's updated_utc.
    private static (int Days, int AgeDays)? StaleAge(string type, string? payloadJson, string? updatedUtc, DateTimeOffset now)
    {
        int? window = null;
        if (Element(payloadJson, "stale_after_days") is { ValueKind: JsonValueKind.Number } sad && sad.TryGetInt32(out var days) && days > 0)
        {
            window = days;
        }
        else if (string.Equals(type, "project_state", StringComparison.Ordinal))
        {
            window = DefaultProjectStateStaleDays;
        }

        if (window is not int limit)
        {
            return null;
        }

        var reference = Field(payloadJson, "updated") ?? Field(payloadJson, "last_verified_at") ?? updatedUtc;
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (reference is null || !DateTimeOffset.TryParse(reference, CultureInfo.InvariantCulture, styles, out var since))
        {
            return null;
        }

        var age = (now - since).TotalDays;
        return age > limit ? (limit, (int)age) : null;
    }

    private static JsonElement? Element(string? json, string name)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) ? value.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
