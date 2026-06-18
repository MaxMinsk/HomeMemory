using System.Net.Http;
using System.Text;
using MemoryMcp.Core.Notes;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Webhook;

/// <summary>
/// Posts note-change events to a configured HTTP endpoint (MEMP-184) as JSON — the same body-free facets MQTT
/// sends. When a secret is set, an <c>X-Memory-Signature: sha256=…</c> header lets the receiver verify the body.
/// Best-effort: <see cref="Publish"/> is fire-and-forget so the write path never blocks on a slow endpoint;
/// <see cref="TrySendAsync"/> awaits a delivery and returns its status (used by the admin webhook test).
/// </summary>
public sealed class WebhookNoteEventSink : INoteEventSink
{
    // A single shared client (no per-event socket churn); per-request timeout via the linked token.
    private static readonly HttpClient Http = new();

    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookNoteEventSink> _logger;

    /// <summary>Creates the sink over the webhook options.</summary>
    /// <param name="options">Webhook configuration (URL, secret, timeout).</param>
    /// <param name="logger">Logger for delivery outcomes.</param>
    public WebhookNoteEventSink(WebhookOptions options, ILogger<WebhookNoteEventSink> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Publish(NoteChangedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_options.Enabled)
        {
            return;
        }

        // Fire-and-forget: the write path must not wait on the endpoint. Outcomes are logged inside the task.
        _ = DeliverAndLogAsync(e);
    }

    /// <summary>
    /// Sends one event and returns the delivered HTTP status code, or null if no URL is configured / the request
    /// failed before a response. Used by the admin webhook-test so the owner can verify config (MEMP-188).
    /// </summary>
    /// <param name="e">The event to send.</param>
    public async Task<int?> TrySendAsync(NoteChangedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_options.Enabled)
        {
            return null;
        }

        using var request = BuildRequest(e);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    private async Task DeliverAndLogAsync(NoteChangedEvent e)
    {
        try
        {
            var status = await TrySendAsync(e).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                _logger.LogDebug("Webhook delivered note-change {Id} ({Op}) -> {Status}.", e.Id, e.Op, status);
            }
            else
            {
                _logger.LogWarning("Webhook delivery for note-change {Id} ({Op}) returned {Status}.", e.Id, e.Op, status);
            }
        }
        catch (Exception ex)
        {
            // Never let a webhook failure surface to the write path.
            _logger.LogWarning(ex, "Webhook delivery for note-change {Id} ({Op}) failed.", e.Id, e.Op);
        }
    }

    private HttpRequestMessage BuildRequest(NoteChangedEvent e)
    {
        var body = NoteEventJson.Serialize(e);
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (_options.Secret is { } secret)
        {
            request.Headers.TryAddWithoutValidation("X-Memory-Signature", WebhookSignature.Compute(secret, body));
        }

        return request;
    }
}
