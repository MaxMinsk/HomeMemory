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

    /// <summary>Builds the client and its connection options from <paramref name="options"/>.</summary>
    /// <param name="options">The MQTT configuration.</param>
    /// <param name="logger">Logger for connect/publish failures.</param>
    public MqttConnection(MqttOptions options, ILogger<MqttConnection> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            await _client.ConnectAsync(_clientOptions, cancellationToken).ConfigureAwait(false);
            return _client.IsConnected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MQTT connect to {Host}:{Port} failed; will retry later.", _options.Host, _options.Port);
            return false;
        }
        finally
        {
            _gate.Release();
        }
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
