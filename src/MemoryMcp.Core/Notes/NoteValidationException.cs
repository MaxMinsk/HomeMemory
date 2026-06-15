namespace MemoryMcp.Core.Notes;

/// <summary>Thrown when a note payload fails schema validation on write; nothing is persisted.</summary>
public sealed class NoteValidationException : Exception
{
    /// <summary>The per-location validation errors.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Creates the exception from a set of validation errors.</summary>
    /// <param name="errors">The validation errors to report.</param>
    public NoteValidationException(IReadOnlyList<string> errors)
        : base("Payload failed schema validation: " + string.Join("; ", errors)) =>
        Errors = errors;
}
