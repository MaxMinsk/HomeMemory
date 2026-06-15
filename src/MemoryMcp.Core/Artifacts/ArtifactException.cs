namespace MemoryMcp.Core.Artifacts;

/// <summary>Thrown when an artifact operation cannot proceed (quota exceeded, source not readable,
/// unknown id, unsafe path). Surfaced to the model as a readable error.</summary>
public sealed class ArtifactException : Exception
{
    /// <summary>Creates the exception with a model-readable message.</summary>
    /// <param name="message">The reason the operation failed.</param>
    public ArtifactException(string message) : base(message)
    {
    }
}
