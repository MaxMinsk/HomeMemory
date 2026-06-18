using System.Text.Json;
using MemoryMcp.Core.Notes;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Mqtt;

/// <summary>
/// Publishes note-change events to an MQTT broker for real-time Home Assistant consumption. Each event is
/// sent as JSON to <c>&lt;prefix&gt;/&lt;domain&gt;/&lt;type&gt;</c> at QoS 0 with retain off. The payload
/// carries only envelope facets (id/domain/type/project/tags/op/ts) — never the note body or any secret.
/// Best-effort throughout: a down broker logs and drops, never crashing a write.
/// </summary>
public sealed class MqttNoteEventSink : INoteEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MqttConnection _connection;
    private readonly string _topicPrefix;
    private readonly ILogger<MqttNoteEventSink> _logger;

    /// <summary>Creates the sink over a shared connection.</summary>
    /// <param name="connection">The shared MQTT connection.</param>
    /// <param name="options">MQTT options (for the topic prefix).</param>
    /// <param name="logger">Logger for unexpected failures.</param>
    public MqttNoteEventSink(MqttConnection connection, MqttOptions options, ILogger<MqttNoteEventSink> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(options);
        _topicPrefix = options.TopicPrefix;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Publish(NoteChangedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        try
        {
            var topic = $"{_topicPrefix}/{e.Domain}/{e.Type}";
            var payload = JsonSerializer.Serialize(
                new { id = e.Id, domain = e.Domain, type = e.Type, project = e.Project, tags = e.Tags, op = e.Op, ts = e.Ts },
                JsonOptions);

            // Fire-and-forget: the write path must not block on the broker. Failures are swallowed inside the task.
            _ = Task.Run(() => _connection.TryPublishAsync(topic, payload, retain: false));
        }
        catch (Exception ex)
        {
            // Defensive: serialization/topic building should not throw, but never let it reach the write path.
            _logger.LogDebug(ex, "Failed to enqueue MQTT note-change event for {Id}.", e.Id);
        }
    }
}
