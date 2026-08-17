namespace MemoryMcp.Core.Skills;

/// <summary>A server-hosted skill: shared "soft craft" guidance agents fetch before authoring a note
/// type, so every agent writes that type the same way. The skill text lives in <paramref name="Body"/>
/// (omitted in list results).</summary>
/// <param name="Key">Stable skill key (e.g. <c>recipe-authoring</c>).</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="TargetType">Note type this skill guides (e.g. <c>recipe</c>); null = general.</param>
/// <param name="Version">Author-managed version (bumped on meaningful change).</param>
/// <param name="Summary">One-line description of what the skill teaches.</param>
/// <param name="Body">The skill content (markdown); null in list results.</param>
/// <param name="Project">Project this skill is specific to (overrides the domain-general one with the same key); null = general.</param>
/// <param name="Domain">The domain the skill was found in. Carried because a skill's DOMAIN and its PROJECT are
/// different axes, and fetching its body needs the domain — inferring one from the other silently looks in the
/// wrong place.</param>
/// <param name="TagsJson">The skill's tags as a JSON array (MEMP-258). Carried because tags are the curated
/// statement of what a skill is FOR, and so the strongest signal available when selecting one for a task.</param>
/// <param name="ResolvedFrom">Which scope answered: <c>project</c>, <c>domain</c> or <c>commons</c>. Null when
/// the skill was listed rather than resolved. Reported because an override and a shared default lead to
/// different decisions, and the body alone does not reveal which one arrived.</param>
public sealed record Skill(string Key, string? Title, string? TargetType, int Version, string? Summary, string? Body, string? Project = null, string? ResolvedFrom = null, string? TagsJson = null, string? Domain = null);
