namespace MemoryMcp.Core.Skills;

/// <summary>One recommended consolidation action for a skill (read-only advice; the agent applies it).</summary>
/// <param name="Key">The skill key the action is about.</param>
/// <param name="Project">The skill's project scope, or null for a domain-general skill.</param>
/// <param name="Action">delete (redundant) / merge (duplicate of another key) / noop.</param>
/// <param name="Reason">Why the action is suggested.</param>
/// <param name="RelatedKey">The other skill involved (the override target, or the canonical duplicate), if any.</param>
public sealed record SkillConsolidationItem(string Key, string? Project, string Action, string Reason, string? RelatedKey);

/// <summary>
/// A consolidation plan for a domain's skills (MEMP-036): flags redundant project overrides (identical to the
/// domain-general skill they shadow) and duplicate skills (identical bodies under different keys). Read-only and
/// reversible — it proposes <c>delete</c>/<c>merge</c>; the agent decides and applies via skill_upsert.
/// </summary>
/// <param name="Domain">The analyzed domain.</param>
/// <param name="Items">The recommended actions (empty when nothing is redundant).</param>
public sealed record SkillConsolidationPlan(string Domain, IReadOnlyList<SkillConsolidationItem> Items);
