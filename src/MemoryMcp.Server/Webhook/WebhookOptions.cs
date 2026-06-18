namespace MemoryMcp.Server.Webhook;

/// <summary>
/// HTTP webhook configuration, read from add-on options (env vars). Opt-in and disabled by default: when no URL is
/// set nothing is posted. When a secret is set, deliveries carry an HMAC-SHA256 signature header (MEMP-184).
/// </summary>
/// <param name="Url">Endpoint to POST change events to (empty = disabled).</param>
/// <param name="Secret">Optional shared secret for the signature header.</param>
/// <param name="TimeoutMs">Per-request timeout in milliseconds.</param>
public sealed record WebhookOptions(string Url, string? Secret, int TimeoutMs)
{
    /// <summary>True when a webhook URL is configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Reads the options from the environment (MEMORY_WEBHOOK_*), falling back to safe defaults.</summary>
    public static WebhookOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("MEMORY_WEBHOOK_URL") ?? string.Empty;
        var secret = NullIfBlank(Environment.GetEnvironmentVariable("MEMORY_WEBHOOK_SECRET"));
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("MEMORY_WEBHOOK_TIMEOUT_MS"), out var parsed) && parsed > 0
            ? parsed
            : 5000;
        return new WebhookOptions(url.Trim(), secret, timeout);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
