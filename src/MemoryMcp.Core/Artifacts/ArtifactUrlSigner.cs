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
    // Browser/read links default to a day (a recipe link should survive a cooking session, not an hour);
    // write-capability (upload) URLs stay short. Both still clamp to [60s, MaxTtlSeconds].
    private const int DefaultReadTtlSeconds = 24 * 3600;
    private const int DefaultUploadTtlSeconds = 3600;
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

    /// <summary>Builds a signed same-origin path for an artifact id (default 1 day TTL; used by the viewer).</summary>
    public string BuildPath(string id, int ttlSeconds = DefaultReadTtlSeconds)
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

    /// <summary>
    /// Builds a signed one-time-ish upload URL (HTTP PUT) bound to exactly these attachment parameters,
    /// so the client cannot change the destination domain/filename/etc. Bytes are PUT directly to the
    /// server, never through the model context.
    /// </summary>
    public string BuildUploadUrl(string domain, string filename, string? contentType, string? noteId, int ttlSeconds = DefaultUploadTtlSeconds)
    {
        var expiry = _clock.GetUtcNow().ToUnixTimeSeconds() + Math.Clamp(ttlSeconds, 60, MaxTtlSeconds);
        var query =
            $"domain={Uri.EscapeDataString(domain)}&filename={Uri.EscapeDataString(filename)}" +
            $"&contentType={Uri.EscapeDataString(contentType ?? string.Empty)}&noteId={Uri.EscapeDataString(noteId ?? string.Empty)}" +
            $"&exp={expiry}&sig={SignUpload(domain, filename, contentType, noteId, expiry)}";
        return (_baseUrl ?? string.Empty) + $"/artifacts/upload?{query}";
    }

    /// <summary>Verifies a signed upload request: the signature matches these exact parameters and has not expired.</summary>
    public bool VerifyUpload(string? domain, string? filename, string? contentType, string? noteId, string? exp, string? sig)
    {
        if (sig is null || string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(filename)
            || !long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry))
        {
            return false;
        }

        if (_clock.GetUtcNow().ToUnixTimeSeconds() > expiry)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(sig), Encoding.UTF8.GetBytes(SignUpload(domain, filename, contentType, noteId, expiry)));
    }

    private string Sign(string id, long expiry)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{id}\n{expiry}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    // Distinct "upload" message prefix so an upload signature can never be replayed as a read signature.
    private string SignUpload(string domain, string filename, string? contentType, string? noteId, long expiry)
    {
        var message = $"upload\n{domain}\n{filename}\n{contentType ?? string.Empty}\n{noteId ?? string.Empty}\n{expiry}";
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
