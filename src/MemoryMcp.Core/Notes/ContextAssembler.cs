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

    /// <summary>Builds the context block, or null if the domain is out of read scope.</summary>
    /// <param name="query">The task query to recall notes for.</param>
    /// <param name="domain">The domain to assemble for.</param>
    /// <param name="limit">Max recall hits.</param>
    /// <param name="includeLinks">Include one-hop neighbors in the recall.</param>
    /// <param name="scope">The caller's request scope.</param>
    /// <param name="project">Optional project: its skills override the domain-general ones of the same key.</param>
    /// <param name="budgetChars">When set, pack recall hits to this snippet-char budget instead of a fixed count (MEMP-176).</param>
    public ContextBlock? Assemble(string query, string domain, int limit, bool includeLinks, RequestScope scope, string? project = null, int? budgetChars = null)
    {
        var guard = new ScopeGuard(scope);
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

        var warnings = new List<string>();
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

        var recall = _notes.Recall(query, domain, limit, guard.RestrictionForSearch(domain), includeLinks, 1, budgetChars);
        return new ContextBlock(domain, ranked, dedupedSkills, recall, AdvisoryPolicy, warnings);
    }

    private static bool AlwaysApply(SearchResult rule) =>
        Element(rule.PayloadJson, "always_apply") is { ValueKind: JsonValueKind.True };

    private static int Priority(SearchResult rule) =>
        Element(rule.PayloadJson, "priority") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var p) ? p : 0;

    private static string? Field(string? json, string name) =>
        Element(json, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

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
