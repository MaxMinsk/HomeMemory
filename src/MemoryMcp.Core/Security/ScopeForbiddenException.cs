namespace MemoryMcp.Core.Security;

/// <summary>Thrown when a caller attempts to access a domain outside its token's scope.</summary>
public sealed class ScopeForbiddenException : Exception
{
    /// <summary>Creates the exception for the forbidden <paramref name="domain"/>.</summary>
    /// <param name="domain">The domain that was denied.</param>
    public ScopeForbiddenException(string domain)
        : base($"Access to domain '{domain}' is not permitted for this token.") =>
        Domain = domain;

    /// <summary>The domain that was denied.</summary>
    public string Domain { get; }
}
