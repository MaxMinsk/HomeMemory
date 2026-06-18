namespace MemoryMcp.Core.Notes;

/// <summary>Thrown when an optimistic-concurrency update loses: the note was modified since the caller
/// read it (its <c>updated_utc</c> revision no longer matches the expected one). Nothing is written. The
/// exception carries the current revision, the last writer, and which fields the caller's write would change
/// (MEMP-182), so the caller can re-read precisely and reconcile instead of blind-retrying.</summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>The note's current <c>updated_utc</c> revision (what to re-read against), or null when the note is gone.</summary>
    public string? CurrentRevision { get; }

    /// <summary>The source agent that last wrote the note, if known.</summary>
    public string? CurrentSourceAgent { get; }

    /// <summary>The fields (title/body/payload/tags) where the caller's write differs from the current copy.</summary>
    public IReadOnlyList<string> ChangedFields { get; }

    /// <summary>Creates the exception with a model-readable message and optional conflict detail.</summary>
    /// <param name="message">What conflicted and what to do.</param>
    /// <param name="currentRevision">The note's current revision.</param>
    /// <param name="currentSourceAgent">Who last wrote it.</param>
    /// <param name="changedFields">Fields the caller's write would change.</param>
    public ConcurrencyException(
        string message, string? currentRevision = null, string? currentSourceAgent = null,
        IReadOnlyList<string>? changedFields = null) : base(message)
    {
        CurrentRevision = currentRevision;
        CurrentSourceAgent = currentSourceAgent;
        ChangedFields = changedFields ?? Array.Empty<string>();
    }

    /// <summary>Builds a "stale update" conflict with a consistent, agent-readable message.</summary>
    /// <param name="currentRevision">The note's current revision.</param>
    /// <param name="currentSourceAgent">Who last wrote it (null = unknown).</param>
    /// <param name="changedFields">Fields the caller's write would change.</param>
    public static ConcurrencyException Stale(string currentRevision, string? currentSourceAgent, IReadOnlyList<string> changedFields)
    {
        var who = string.IsNullOrEmpty(currentSourceAgent) ? "another writer" : currentSourceAgent;
        var fields = changedFields.Count > 0 ? $" Your write would change: {string.Join(", ", changedFields)}." : string.Empty;
        return new ConcurrencyException(
            $"Stale update: this note changed since you read it (current revision {currentRevision}, last written by {who}).{fields} Re-read and retry.",
            currentRevision, currentSourceAgent, changedFields);
    }

    /// <summary>Builds a conflict for "I expected to update an existing note, but none is there now".</summary>
    public static ConcurrencyException Missing() =>
        new("Stale update: you passed expectedRevision but no matching note exists (it was never created or was removed). Re-read and retry.");
}
