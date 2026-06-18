using System.Security.Cryptography;
using System.Text;

namespace MemoryMcp.Core.Notes;

/// <summary>Computes the webhook payload signature (MEMP-184) so a receiver can verify a delivery came from this
/// server: <c>sha256=&lt;hex&gt;</c>, an HMAC-SHA256 of the raw JSON body keyed by the shared secret.</summary>
public static class WebhookSignature
{
    /// <summary>Returns the <c>sha256=&lt;hex&gt;</c> signature for a payload under the given secret.</summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="payload">The exact JSON body that will be sent.</param>
    public static string Compute(string secret, string payload)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(payload);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
