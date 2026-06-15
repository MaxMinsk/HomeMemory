namespace MemoryMcp.Core.Confirmation;

/// <summary>Thrown when a confirmation request or resolution is invalid (unknown/expired token,
/// already resolved, unsupported action, missing target). Surfaced to the model as a readable error.</summary>
public sealed class ConfirmationException : Exception
{
    /// <summary>Creates the exception with a model-readable message.</summary>
    /// <param name="message">The reason the confirmation could not proceed.</param>
    public ConfirmationException(string message) : base(message)
    {
    }
}
