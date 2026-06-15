namespace MemoryMcp.Core.Schemas;

/// <summary>Thrown when an agent-authored schema cannot be accepted: not a valid JSON Schema, a
/// built-in (read-only) type, or a change to a version that existing notes already use. Surfaced to
/// the model as a readable error.</summary>
public sealed class SchemaAuthoringException : Exception
{
    /// <summary>Creates the exception with a model-readable message.</summary>
    /// <param name="message">The reason the schema was rejected.</param>
    public SchemaAuthoringException(string message) : base(message)
    {
    }
}
