using MemoryMcp.Core.Security;

namespace MemoryMcp.Server.Security;

/// <summary>
/// The single domain-authorization facade for the request in flight (MEMP-102). Every tool and HTTP
/// endpoint authorizes through this — none constructs a <see cref="ScopeGuard"/> itself — so scope
/// enforcement lives in one place and a new tool/endpoint can't accidentally skip a check or bypass a
/// domain. It reads the ambient <see cref="RequestScope"/> per call (the accessor is request-aware),
/// so it is safe as a singleton.
/// </summary>
public sealed class RequestAuthorizer
{
    private readonly IScopeAccessor _scope;

    /// <summary>Creates the authorizer over the scope accessor.</summary>
    /// <param name="scope">Supplies the active request scope.</param>
    public RequestAuthorizer(IScopeAccessor scope) => _scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>The active request scope (for callers that need it directly, e.g. capabilities).</summary>
    public RequestScope Scope => _scope.Current;

    // A fresh guard bound to the scope as of *this* call (the scope can differ per request).
    private ScopeGuard Guard => new(_scope.Current);

    /// <summary>Throws <see cref="ScopeForbiddenException"/> if the caller may not WRITE <paramref name="domain"/>.</summary>
    /// <param name="domain">The target domain.</param>
    public void AuthorizeWrite(string domain) => Guard.Authorize(domain);

    /// <summary>True if the caller may READ <paramref name="domain"/> (in scope, or the shared commons).</summary>
    /// <param name="domain">The target domain.</param>
    public bool CanRead(string domain) => Guard.IsAllowed(domain);

    /// <summary>Domain restriction for a read/search (null = unrestricted). Throws for an explicit out-of-scope domain.</summary>
    /// <param name="requestedDomain">The domain filter the caller asked for, if any.</param>
    public IReadOnlyCollection<string>? ReadRestriction(string? requestedDomain) => Guard.RestrictionForSearch(requestedDomain);

    /// <summary>Domain restriction for a write-scoped listing (allowed domains only, no commons; null = unrestricted).</summary>
    public IReadOnlyCollection<string>? WriteRestriction() => Guard.WriteRestriction();
}
