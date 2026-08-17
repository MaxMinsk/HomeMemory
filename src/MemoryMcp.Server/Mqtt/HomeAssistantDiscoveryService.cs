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
    private readonly OperationMetrics _metrics;
    private readonly ILogger<HomeAssistantDiscoveryService> _logger;

    /// <summary>Creates the discovery service.</summary>
    /// <param name="connection">The shared MQTT connection.</param>
    /// <param name="diagnostics">Source of the stats numbers.</param>
    /// <param name="metrics">Source of the load numbers (MEMP-247).</param>
    /// <param name="logger">Logger for failures.</param>
    public HomeAssistantDiscoveryService(MqttConnection connection, DiagnosticsService diagnostics,
        OperationMetrics metrics, ILogger<HomeAssistantDiscoveryService> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
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
        // What the server COSTS, next to what it stores (MEMP-247) — the box is shared, so these belong beside
        // the other add-ons' figures in Home Assistant rather than behind a tool call only an agent can make.
        await PublishConfigAsync("memory_bytes", "Memory Resident Memory", "B", "value_json.memory_bytes", device, cancellationToken).ConfigureAwait(false);
        await PublishConfigAsync("cpu_seconds", "Memory CPU Time", "s", "value_json.cpu_seconds", device, cancellationToken).ConfigureAwait(false);
        await PublishConfigAsync("search_p95_ms", "Memory Search p95", "ms", "value_json.search_p95_ms", device, cancellationToken).ConfigureAwait(false);
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
            var load = _metrics.Snapshot();
            var search = load.Operations.FirstOrDefault(op => op.Operation == "notes_search");
            var state = JsonSerializer.Serialize(
                new
                {
                    note_count = stats.NoteCount,
                    db_size_bytes = stats.DbSizeBytes,
                    attachment_count = stats.AttachmentCount,
                    memory_bytes = load.WorkingSetBytes,
                    cpu_seconds = load.CpuSeconds,
                    search_p95_ms = search?.P95Ms ?? 0,
                },
                JsonOptions);
            await _connection.TryPublishAsync(StateTopic, state, retain: true, cancellationToken).ConfigureAwait(false);

            var attributes = JsonSerializer.Serialize(
                new
                {
                    notes_by_domain = ToStringKeyed(stats.NotesByDomain),
                    notes_by_type = ToStringKeyed(stats.NotesByType),
                    db_size_mb = Math.Round(stats.DbSizeBytes / 1048576.0, 2),
                    uptime_seconds = load.UptimeSeconds,
                    managed_heap_mb = Math.Round(load.ManagedHeapBytes / 1048576.0, 2),
                    // Every measured operation, so a slow one that has no sensor of its own is still visible.
                    operations = load.Operations.ToDictionary(
                        op => op.Operation,
                        op => new { count = op.Count, p50_ms = op.P50Ms, p95_ms = op.P95Ms, max_ms = op.MaxMs },
                        StringComparer.Ordinal),
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
