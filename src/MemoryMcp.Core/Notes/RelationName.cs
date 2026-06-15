using System.Text.RegularExpressions;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Validates note-link relation names. Convention (MEMP-039): a relation reads as an active-voice,
/// present-tense verb phrase from source to target, in <c>lower_snake_case</c> — e.g. <c>uses</c>,
/// <c>supersedes</c>, <c>depends_on</c>, <c>derived_from</c>, <c>in_sprint</c>, <c>rendered_from</c>.
/// </summary>
public static class RelationName
{
    private static readonly Regex Pattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>A one-line description of the expected format, for error messages.</summary>
    public const string Expectation = "use an active-voice lower_snake_case verb, e.g. uses, depends_on, in_sprint";

    /// <summary>Returns true if <paramref name="rel"/> matches the relation-name convention.</summary>
    /// <param name="rel">The candidate relation name.</param>
    public static bool IsValid(string? rel) => !string.IsNullOrWhiteSpace(rel) && Pattern.IsMatch(rel);
}
