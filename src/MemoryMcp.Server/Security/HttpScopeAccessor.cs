using MemoryMcp.Core.Security;
using Microsoft.AspNetCore.Http;

namespace MemoryMcp.Server.Security;

/// <summary>Reads the per-request <see cref="RequestScope"/> stashed by the bearer-auth middleware.</summary>
public sealed class HttpScopeAccessor : IScopeAccessor
{
    /// <summary>HttpContext.Items key under which the resolved scope is stored.</summary>
    public const string ScopeKey = "memory.scope";

    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Creates the accessor over the ambient HTTP context.</summary>
    /// <param name="httpContextAccessor">Accessor for the current HTTP context.</param>
    public HttpScopeAccessor(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    // Fail closed (MEMP-102): if the auth middleware did not stash a scope, deny everything rather than
    // defaulting to unrestricted — a future route that forgets the middleware must not leak data.
    private static readonly RequestScope DenyAll = RequestScope.ForDomains(Array.Empty<string>());

    /// <inheritdoc />
    public RequestScope Current =>
        _httpContextAccessor.HttpContext?.Items[ScopeKey] as RequestScope ?? DenyAll;
}
