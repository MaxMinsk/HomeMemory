using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Security;
using Microsoft.AspNetCore.Http;

namespace MemoryMcp.Server.Security;

/// <summary>
/// Resolves an HTTP request's bearer token to a <see cref="RequestScope"/>. The env
/// <c>MEMORY_BEARER_TOKEN</c> is the root token (scoped by <c>MEMORY_ALLOWED_DOMAINS</c>); any other token
/// is looked up in the <see cref="TokenStore"/> for its per-token domain scope (MEMP-032). When no env
/// token is set (explicit dev mode), every request is unrestricted.
/// </summary>
public sealed class BearerAuthenticator
{
    private readonly byte[]? _envToken;
    private readonly RequestScope _envScope;
    private readonly TokenStore _tokens;

    /// <summary>Creates the authenticator from the env root token + its allowed domains and the token store.</summary>
    public BearerAuthenticator(string? envToken, IEnumerable<string> envAllowedDomains, TokenStore tokens)
    {
        _envToken = string.IsNullOrWhiteSpace(envToken) ? null : Encoding.UTF8.GetBytes(envToken);
        var domains = envAllowedDomains.ToArray();
        _envScope = domains.Length == 0 ? RequestScope.Unrestricted : RequestScope.ForDomains(domains);
        _tokens = tokens;
    }

    /// <summary>
    /// Resolves the request's bearer to its scope, or <c>null</c> if unauthenticated/unknown/revoked.
    /// In dev mode (no env token) every request resolves to an unrestricted scope.
    /// </summary>
    public RequestScope? Authenticate(HttpContext context)
    {
        if (_envToken is null)
        {
            return RequestScope.Unrestricted; // dev: auth not enforced (see the fail-closed startup check)
        }

        var presented = ExtractBearer(context);
        if (presented is null)
        {
            return null;
        }

        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _envToken))
        {
            return _envScope;
        }

        var record = _tokens.Resolve(presented);
        if (record is null)
        {
            return null;
        }

        var allowed = TokenStore.AllowedDomains(record.Domains);
        return allowed is null ? RequestScope.Unrestricted : RequestScope.ForDomains(allowed);
    }

    private static string? ExtractBearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
