using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MemoryMcp.Core.Artifacts;

/// <summary>
/// Signs short-lived capability URLs for artifacts so browser links never carry the long-lived bearer
/// token (which would leak into history/logs/referrer). A link is <c>/artifacts/{id}?exp={unix}&amp;sig={hmac}</c>;
/// the HMAC is keyed by the server's bearer secret and bound to the id + expiry.
/// </summary>
public sealed class ArtifactUrlSigner
{
    private const int DefaultTtlSeconds = 3600;
    private const int MaxTtlSeconds = 7 * 24 * 3600;
    private readonly byte[] _key;
    private readonly TimeProvider _clock;
    private readonly string? _baseUrl;

    /// <summary>Creates the signer. The secret keys the HMAC (the bearer token; a fixed fallback when unset).
    /// <paramref name="baseUrl"/> (the public origin) makes <see cref="BuildUrl"/> absolute for sharing.</summary>
    public ArtifactUrlSigner(string? secret, TimeProvider clock, string? baseUrl = null)
    {
        _key = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(secret) ? "memory-mcp-unsigned" : secret);
        _clock = clock;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
    }

    /// <summary>Builds a signed same-origin path for an artifact id (default 1h TTL; used by the viewer).</summary>
    public string BuildPath(string id, int ttlSeconds = DefaultTtlSeconds)
    {
        var expiry = _clock.GetUtcNow().ToUnixTimeSeconds() + Math.Clamp(ttlSeconds, 60, MaxTtlSeconds);
        return $"/artifacts/{id}?exp={expiry}&sig={Sign(id, expiry)}";
    }

    /// <summary>Builds a signed URL, absolute when a public base URL is configured — for handing to a human/agent.</summary>
    public string BuildUrl(string id, int ttlSeconds) => (_baseUrl ?? string.Empty) + BuildPath(id, ttlSeconds);

    /// <summary>Verifies a signed request: the signature matches the id+expiry and has not expired.</summary>
    public bool Verify(string id, string? exp, string? sig)
    {
        if (sig is null || !long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry))
        {
            return false;
        }

        if (_clock.GetUtcNow().ToUnixTimeSeconds() > expiry)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(sig), Encoding.UTF8.GetBytes(Sign(id, expiry)));
    }

    private string Sign(string id, long expiry)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{id}\n{expiry}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
