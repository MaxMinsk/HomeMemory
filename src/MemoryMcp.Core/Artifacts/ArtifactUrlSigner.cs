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
    private const int TtlSeconds = 3600;
    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    /// <summary>Creates the signer. The secret keys the HMAC (the bearer token; a fixed fallback when unset).</summary>
    public ArtifactUrlSigner(string? secret, TimeProvider clock)
    {
        _key = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(secret) ? "memory-mcp-unsigned" : secret);
        _clock = clock;
    }

    /// <summary>Builds a short-lived signed path for an artifact id.</summary>
    public string BuildPath(string id)
    {
        var expiry = _clock.GetUtcNow().ToUnixTimeSeconds() + TtlSeconds;
        return $"/artifacts/{id}?exp={expiry}&sig={Sign(id, expiry)}";
    }

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
