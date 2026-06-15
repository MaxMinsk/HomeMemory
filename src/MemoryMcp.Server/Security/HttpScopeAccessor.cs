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

    /// <inheritdoc />
    public RequestScope Current =>
        _httpContextAccessor.HttpContext?.Items[ScopeKey] as RequestScope ?? RequestScope.Unrestricted;
}
