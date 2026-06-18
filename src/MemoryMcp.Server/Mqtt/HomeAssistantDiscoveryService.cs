using System.Globalization;
using System.Text.Json;
using MemoryMcp.Core.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Mqtt;

/// <summary>
/// Publishes Home Assistant MQTT-discovery configs and periodic state for a few memory stats (MEMP-056).
/// On startup it announces sensors under <c>homeassistant/sensor/memory_&lt;key&gt;/config</c> (retained), then
/// republishes state every ~60s. Best-effort: every failure is swallowed so the host never crashes.
/// </summary>
public sealed class HomeAssistantDiscoveryService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string StateTopic = "memory/stats";
    private const string AttributesTopic = "memory/stats/attributes";

    private readonly MqttConnection _connection;
    private readonly DiagnosticsService _diagnostics;
    private readonly ILogger<HomeAssistantDiscoveryService> _logger;

    /// <summary>Creates the discovery service.</summary>
    /// <param name="connection">The shared MQTT connection.</param>
    /// <param name="diagnostics">Source of the stats numbers.</param>
    /// <param name="logger">Logger for failures.</param>
    public HomeAssistantDiscoveryService(MqttConnection connection, DiagnosticsService diagnostics, ILogger<HomeAssistantDiscoveryService> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PublishDiscoveryAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PublishStateAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
        }
    }

    // Announces the sensors to Home Assistant. Retained so HA rediscovers them after a restart.
    private async Task PublishDiscoveryAsync(CancellationToken cancellationToken)
    {
        var device = new
        {
            identifiers = new[] { "memory_mcp" },
            name = "Memory MCP",
            manufacturer = "HomeMemory",
            model = "Memory MCP add-on",
        };

        await PublishConfigAsync("note_count", "Memory Notes", "notes", "value_json.note_count", device, cancellationToken).ConfigureAwait(false);
        await PublishConfigAsync("db_size_bytes", "Memory DB Size", "B", "value_json.db_size_bytes", device, cancellationToken).ConfigureAwait(false);
        await PublishConfigAsync("attachment_count", "Memory Attachments", "files", "value_json.attachment_count", device, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishConfigAsync(string key, string name, string unit, string valueTemplate, object device, CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["unique_id"] = "memory_" + key,
            ["state_topic"] = StateTopic,
            ["json_attributes_topic"] = AttributesTopic,
            ["unit_of_measurement"] = unit,
            ["value_template"] = "{{ " + valueTemplate + " }}",
            ["state_class"] = "measurement",
            ["device"] = device,
        };
        var topic = $"homeassistant/sensor/memory_{key}/config";
        await _connection.TryPublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), retain: true, cancellationToken).ConfigureAwait(false);
    }

    // Publishes the current stat values (state) plus a per-domain/per-type breakdown (attributes).
    private async Task PublishStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stats = _diagnostics.Snapshot();
            var state = JsonSerializer.Serialize(
                new
                {
                    note_count = stats.NoteCount,
                    db_size_bytes = stats.DbSizeBytes,
                    attachment_count = stats.AttachmentCount,
                },
                JsonOptions);
            await _connection.TryPublishAsync(StateTopic, state, retain: true, cancellationToken).ConfigureAwait(false);

            var attributes = JsonSerializer.Serialize(
                new
                {
                    notes_by_domain = ToStringKeyed(stats.NotesByDomain),
                    notes_by_type = ToStringKeyed(stats.NotesByType),
                    db_size_mb = Math.Round(stats.DbSizeBytes / 1048576.0, 2),
                },
                JsonOptions);
            await _connection.TryPublishAsync(AttributesTopic, attributes, retain: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish Home Assistant memory stats.");
        }
    }

    private static Dictionary<string, long> ToStringKeyed(IReadOnlyDictionary<string, long> source)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}
