namespace MemoryMcp.Core.Query;

/// <summary>Thrown when a search <c>filter</c> expression cannot be parsed or references an unknown field.</summary>
public sealed class FilterException : Exception
{
    /// <summary>Creates the exception with a human-readable reason.</summary>
    /// <param name="message">What is wrong with the filter.</param>
    public FilterException(string message) : base(message)
    {
    }
}
