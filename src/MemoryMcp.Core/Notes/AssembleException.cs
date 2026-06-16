namespace MemoryMcp.Core.Notes;

/// <summary>Thrown when an atomic assemble cannot complete (e.g. a link target is missing or a relation
/// is invalid); the whole transaction is rolled back, so nothing is persisted.</summary>
public sealed class AssembleException : Exception
{
    /// <summary>Creates the exception with a model-readable message.</summary>
    public AssembleException(string message) : base(message)
    {
    }
}
