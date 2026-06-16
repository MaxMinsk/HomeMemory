using MemoryMcp.Core.Naming;

namespace MemoryMcp.Core.Security;

/// <summary>Enforces a <see cref="RequestScope"/>: authorizes domain access and computes search restrictions.</summary>
public sealed class ScopeGuard
{
    /// <summary>The world-readable commons domain: readable by every scope (shared rules/skills), but only
    /// writable by a caller it is actually in scope for (root, or a token explicitly scoped to it). MEMP shared domain.</summary>
    public const string CommonsDomain = "commons";

    private readonly RequestScope _scope;

    /// <summary>Creates a guard for the given scope.</summary>
    /// <param name="scope">The active request scope.</param>
    public ScopeGuard(RequestScope scope) => _scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>True if the caller may READ <paramref name="domain"/> — in scope, or the shared commons.</summary>
    public bool IsAllowed(string domain)
    {
        var normalized = Identifiers.Normalize(domain);
        return _scope.IsUnrestricted || _scope.AllowedDomains.Contains(normalized) || normalized == CommonsDomain;
    }

    /// <summary>Throws <see cref="ScopeForbiddenException"/> if the caller may not WRITE <paramref name="domain"/>.
    /// The commons is not auto-writable — writing it requires being explicitly in scope for it.</summary>
    public void Authorize(string domain)
    {
        if (!IsInScope(domain))
        {
            throw new ScopeForbiddenException(domain);
        }
    }

    /// <summary>
    /// Domain restriction for a READ/search: <c>null</c> = no restriction (unrestricted scope, or an
    /// in-scope/commons explicit domain filter); otherwise the allowed domains plus the commons (so shared
    /// rules surface). Throws if an explicit out-of-scope domain was requested.
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
            if (!IsAllowed(requestedDomain))
            {
                throw new ScopeForbiddenException(requestedDomain);
            }

            return null;
        }

        return _scope.AllowedDomains.Append(CommonsDomain).ToList();
    }

    /// <summary>Domain restriction for destructive/write-scoped listing (e.g. confirmations): the allowed
    /// domains only — the commons is excluded (it is read-shared, not write-shared). Null = unrestricted.</summary>
    public IReadOnlyCollection<string>? WriteRestriction() =>
        _scope.IsUnrestricted ? null : _scope.AllowedDomains;

    private bool IsInScope(string domain) =>
        _scope.IsUnrestricted || _scope.AllowedDomains.Contains(Identifiers.Normalize(domain));
}
