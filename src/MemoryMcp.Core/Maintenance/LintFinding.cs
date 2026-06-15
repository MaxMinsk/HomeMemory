namespace MemoryMcp.Core.Maintenance;

/// <summary>One data-quality issue found by <see cref="NotesLinter"/> — a suggestion, never a change.</summary>
/// <param name="NoteId">The note the finding is about.</param>
/// <param name="Title">That note's title, if any.</param>
/// <param name="Domain">That note's domain.</param>
/// <param name="Type">That note's type.</param>
/// <param name="Rule">The rule that fired (e.g. <c>no_tags</c>, <c>no_dedup_key</c>, <c>broken_link</c>).</param>
/// <param name="Severity"><c>warn</c> or <c>info</c>.</param>
/// <param name="Message">Human-readable description of the issue.</param>
public sealed record LintFinding(string NoteId, string? Title, string Domain, string Type, string Rule, string Severity, string Message);
