namespace MemoryMcp.Core.Security;

/// <summary>The domain access scope for a single request (derived from the caller's token).</summary>
public sealed class RequestScope
{
    private RequestScope(bool isUnrestricted, IReadOnlySet<string> allowedDomains)
    {
        IsUnrestricted = isUnrestricted;
        AllowedDomains = allowedDomains;
    }

    /// <summary>A trusted scope with access to every domain (e.g. local stdio).</summary>
    public static RequestScope Unrestricted { get; } = new(true, new HashSet<string>());

    /// <summary>True when the caller may access any domain.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>The domains the caller may access (empty when unrestricted).</summary>
    public IReadOnlySet<string> AllowedDomains { get; }

    /// <summary>Creates a restricted scope limited to <paramref name="domains"/>.</summary>
    /// <param name="domains">The domains the caller may access.</param>
    public static RequestScope ForDomains(IEnumerable<string> domains) =>
        new(false, new HashSet<string>(domains, StringComparer.Ordinal));
}
