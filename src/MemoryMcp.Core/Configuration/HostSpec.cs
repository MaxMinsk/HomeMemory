namespace MemoryMcp.Core.Configuration;

/// <summary>
/// Parses a configured broker/server address into the bare host and optional port a client library wants
/// (MEMP-267).
/// <para><b>Why this is forgiving.</b> A network address is written with a scheme nearly everywhere — in docs,
/// in other integrations' settings, in the broker's own UI — so <c>mqtt://192.168.0.131</c> is the most likely
/// thing to be typed into a host field. Passed through verbatim it becomes a DNS lookup for a name containing
/// slashes, which fails as "host not found" while the broker sits right there at that address. Accepting the
/// obvious forms costs a few lines; rejecting them costs an afternoon.</para>
/// <para>It lives in Core, away from the MQTT code it serves, because it is pure string handling and the test
/// project deliberately does not link the server assembly.</para>
/// </summary>
public static class HostSpec
{
    private static readonly string[] Schemes = ["mqtts://", "mqtt://", "tcp://", "ssl://", "wss://", "ws://"];

    /// <summary>
    /// Splits a configured address into host and optional port. A blank input yields an empty host, which the
    /// caller reports rather than silently treating as a default.
    /// </summary>
    /// <param name="configured">The raw configured value.</param>
    public static (string Host, int? Port) Parse(string? configured)
    {
        var value = configured?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return (string.Empty, null);
        }

        foreach (var scheme in Schemes)
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
                break;
            }
        }

        value = value.TrimEnd('/');

        // A trailing :port — but never an IPv6 literal, which is full of colons. Mistaking its last group for a
        // port would connect to the wrong address silently, which is worse than not supporting the form at all.
        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':', StringComparison.Ordinal) == colon
            && int.TryParse(value[(colon + 1)..], out var port) && port > 0)
        {
            return (value[..colon], port);
        }

        return (value, null);
    }
}
