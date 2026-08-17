using MQTTnet;
using MQTTnet.Protocol;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Mqtt;

/// <summary>
/// A best-effort, lazily-connecting MQTT client wrapper shared by the note-change sink and the Home
/// Assistant discovery service. All failures are swallowed and logged: a broker that is down or flaky
/// must never crash startup or fail a write. Reconnects lazily on the next publish.
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly ILogger<MqttConnection> _logger;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _clientOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private bool _reportedFailure;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    /// <summary>
    /// How long to wait after a failed connect before trying again.
    /// <para>Without this, a connect is attempted on EVERY publish, and Home Assistant discovery publishes
    /// several messages back to back — so a broker that rejects the credentials saw the same client id
    /// reconnect eight times in one second, forever. A rejection is a standing answer, not a transient one;
    /// hammering the broker cannot change it and only fills its log.</para>
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Builds the client and its connection options from <paramref name="options"/>.</summary>
    /// <param name="options">The MQTT configuration.</param>
    /// <param name="logger">Logger for connect/publish failures.</param>
    /// <param name="timeProvider">Clock used to space out reconnection attempts.</param>
    public MqttConnection(MqttOptions options, ILogger<MqttConnection> logger, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = timeProvider ?? TimeProvider.System;
        _client = new MqttClientFactory().CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithClientId("memory-mcp-" + Guid.NewGuid().ToString("N")[..8])
            .WithTcpServer(_options.Host, _options.Port)
            .WithCleanSession(true);
        if (_options.Username is not null)
        {
            builder = builder.WithCredentials(_options.Username, _options.Password ?? string.Empty);
        }

        _clientOptions = builder.Build();
    }

    /// <summary>
    /// Publishes a message best-effort: ensures a connection (reconnecting lazily), then sends. Any failure
    /// is logged and swallowed. Returns true only when the publish was accepted by the broker.
    /// </summary>
    /// <param name="topic">The topic to publish to.</param>
    /// <param name="payload">The UTF-8 payload.</param>
    /// <param name="retain">Whether the broker should retain the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> TryPublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(retain)
                .Build();
            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MQTT publish to {Topic} failed; dropping (broker unavailable?).", topic);
            return false;
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
            {
                return true;
            }

            if (_clock.GetUtcNow() < _nextAttempt)
            {
                return false;
            }

            // The result code is inspected, not just exceptions: a broker that REFUSES a connection answers
            // with a CONNACK code, and the client reports that as a returned result rather than by throwing.
            // Watching only the catch block therefore missed the single most common failure — a refusal — and
            // sent it to silence, which is how "not authorised" appeared in the broker log and nowhere in ours.
            var result = await _client.ConnectAsync(_clientOptions, cancellationToken).ConfigureAwait(false);
            if (result?.ResultCode is not MqttClientConnectResultCode.Success || !_client.IsConnected)
            {
                _nextAttempt = _clock.GetUtcNow() + RetryDelay;
                ReportFailure(null, result?.ResultCode.ToString() ?? "no result", result?.ReasonString);
                return false;
            }

            _nextAttempt = DateTimeOffset.MinValue;
            if (_reportedFailure)
            {
                _reportedFailure = false;
                _logger.LogInformation("MQTT connected to {Host}:{Port}.", _options.Host, _options.Port);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The FIRST failure is a warning, the rest are debug. Every failure used to be debug, so a broker
            // that could not be reached produced complete silence at the default log level — and "MQTT is on
            // but no device appeared in Home Assistant" had nothing anywhere to explain it. Only the first is
            // raised, because this is retried on every publish and a warning per attempt would be its own problem.
            _nextAttempt = _clock.GetUtcNow() + RetryDelay;
            ReportFailure(ex, ex.GetType().Name, ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Named separately so the connect path stays readable: the FIRST failure explains itself in full, the
    // rest are debug, because this is retried indefinitely and a warning per attempt is its own problem.
    private void ReportFailure(Exception? exception, string reason, string? detail)
    {
        if (_reportedFailure)
        {
            _logger.LogDebug(exception, "MQTT connect to {Host}:{Port} failed ({Reason}); will retry later.",
                _options.Host, _options.Port, reason);
            return;
        }

        _reportedFailure = true;

        // The username is named and the password only described. "not authorised" from a broker means the same
        // thing whether the wrong credentials were sent or NONE were, and those need opposite fixes — so say
        // which of the two happened rather than leaving it to be guessed.
        _logger.LogWarning(exception,
            "MQTT connect to {Host}:{Port} REFUSED with {Reason}{Detail}, so no Home Assistant sensors will "
            + "appear. Sent username {Username} and {PasswordState}. A 'NotAuthorized' with a username means the "
            + "broker got the credentials and refused them — check the login exists on the broker (for the Home "
            + "Assistant Mosquitto add-on that is a Home Assistant USERNAME, case-sensitive, not a display name). "
            + "A 'NotAuthorized' with username (none) means mqtt_username never reached us and we connected "
            + "anonymously. Retrying at most every {RetrySeconds}s from here on.",
            _options.Host, _options.Port, reason,
            string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})",
            _options.Username is null ? "(none)" : $"'{_options.Username}'",
            _options.Password is null ? "no password" : $"a password of {_options.Password.Length} characters",
            RetryDelay.TotalSeconds);
    }

    /// <summary>Disconnects (best-effort) and disposes the underlying client.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT disconnect failed during shutdown.");
        }

        _client.Dispose();
        _gate.Dispose();
    }
}
