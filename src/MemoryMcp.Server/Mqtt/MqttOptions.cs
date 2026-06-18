namespace MemoryMcp.Server.Mqtt;

/// <summary>
/// MQTT publishing configuration, read from add-on options (env vars). Opt-in and disabled by default:
/// when <see cref="Enabled"/> is false nothing connects and no MQTT dependency is exercised at runtime.
/// </summary>
/// <param name="Enabled">Whether real-time MQTT publishing is on.</param>
/// <param name="Host">Broker host.</param>
/// <param name="Port">Broker port (default 1883).</param>
/// <param name="Username">Optional broker username.</param>
/// <param name="Password">Optional broker password.</param>
/// <param name="TopicPrefix">Topic prefix for note-change events (default <c>memory</c>).</param>
public sealed record MqttOptions(
    bool Enabled, string Host, int Port, string? Username, string? Password, string TopicPrefix)
{
    /// <summary>Reads the options from the environment (MEMORY_MQTT_*), falling back to safe defaults.</summary>
    public static MqttOptions FromEnvironment()
    {
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable("MEMORY_MQTT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
        var host = Environment.GetEnvironmentVariable("MEMORY_MQTT_HOST") ?? string.Empty;
        var port = int.TryParse(Environment.GetEnvironmentVariable("MEMORY_MQTT_PORT"), out var parsed) && parsed > 0
            ? parsed
            : 1883;
        var username = NullIfBlank(Environment.GetEnvironmentVariable("MEMORY_MQTT_USERNAME"));
        var password = NullIfBlank(Environment.GetEnvironmentVariable("MEMORY_MQTT_PASSWORD"));
        var prefix = NullIfBlank(Environment.GetEnvironmentVariable("MEMORY_MQTT_TOPIC_PREFIX")) ?? "memory";
        return new MqttOptions(enabled, host, port, username, password, prefix);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
