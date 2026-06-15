namespace MemoryMcp.Core.Notes;

/// <summary>Thrown when an optimistic-concurrency update loses: the note was modified since the caller
/// read it (its <c>updated_utc</c> revision no longer matches <c>expectedUpdatedUtc</c>). The caller
/// should re-read and retry. Nothing is written.</summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Creates the exception with a model-readable message.</summary>
    /// <param name="message">What conflicted and what to do.</param>
    public ConcurrencyException(string message) : base(message)
    {
    }
}
