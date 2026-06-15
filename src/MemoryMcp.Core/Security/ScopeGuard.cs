namespace MemoryMcp.Core.Security;

/// <summary>Enforces a <see cref="RequestScope"/>: authorizes domain access and computes search restrictions.</summary>
public sealed class ScopeGuard
{
    private readonly RequestScope _scope;

    /// <summary>Creates a guard for the given scope.</summary>
    /// <param name="scope">The active request scope.</param>
    public ScopeGuard(RequestScope scope) => _scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>True if the caller may access <paramref name="domain"/>.</summary>
    public bool IsAllowed(string domain) => _scope.IsUnrestricted || _scope.AllowedDomains.Contains(domain);

    /// <summary>Throws <see cref="ScopeForbiddenException"/> if <paramref name="domain"/> is out of scope.</summary>
    public void Authorize(string domain)
    {
        if (!IsAllowed(domain))
        {
            throw new ScopeForbiddenException(domain);
        }
    }

    /// <summary>
    /// Computes the domain restriction to apply to a search. Returns <c>null</c> when no restriction is
    /// needed (unrestricted scope, or an in-scope explicit domain filter); otherwise the allowed set.
    /// Throws if an explicit out-of-scope domain was requested.
    /// </summary>
    /// <param name="requestedDomain">The domain filter the caller asked for, if any.</param>
    public IReadOnlyCollection<string>? RestrictionForSearch(string? requestedDomain)
    {
        if (_scope.IsUnrestricted)
        {
            return null;
        }

        if (requestedDomain is not null)
        {
            Authorize(requestedDomain);
            return null;
        }

        return _scope.AllowedDomains;
    }
}
