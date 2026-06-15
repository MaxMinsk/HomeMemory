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
public sealed record Skill(string Key, string? Title, string? TargetType, int Version, string? Summary, string? Body);
