using MemoryMcp.Core.Configuration;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-267: the add-on's host field has to accept what a person actually types.
/// <para>MQTTnet wants a bare host and a port. A broker address is written with a scheme almost everywhere
/// else, so <c>mqtt://192.168.0.131</c> is the single most likely thing to be pasted in — and it went to the
/// resolver verbatim and failed as an unknown name, with the broker sitting right there.</para>
/// </summary>
public class MqttHostNormalizationTests
{
    [Theory]
    [InlineData("mqtt://192.168.0.131", "192.168.0.131", null)]
    [InlineData("mqtts://broker.local", "broker.local", null)]
    [InlineData("tcp://10.0.0.5", "10.0.0.5", null)]
    [InlineData("ws://broker.local/", "broker.local", null)]
    [InlineData("MQTT://Broker.Local", "Broker.Local", null)]
    [InlineData("core-mosquitto", "core-mosquitto", null)]
    [InlineData("  192.168.0.131  ", "192.168.0.131", null)]
    public void A_scheme_or_stray_whitespace_is_stripped_from_the_host(string configured, string host, int? port)
    {
        var normalized = HostSpec.Parse(configured);

        Assert.Equal(host, normalized.Host);
        Assert.Equal(port, normalized.Port);
    }

    /// <summary>A <c>host:port</c> form is honoured, since that is how a broker is usually quoted.</summary>
    [Theory]
    [InlineData("mqtt://192.168.0.131:1884", "192.168.0.131", 1884)]
    [InlineData("broker.local:8883", "broker.local", 8883)]
    public void A_port_in_the_host_field_is_understood(string configured, string host, int port)
    {
        var normalized = HostSpec.Parse(configured);

        Assert.Equal(host, normalized.Host);
        Assert.Equal(port, normalized.Port);
    }

    /// <summary>
    /// An IPv6 literal is full of colons and must not have its last group mistaken for a port — that would
    /// silently connect to the wrong address rather than fail loudly.
    /// </summary>
    [Fact]
    public void An_ipv6_literal_is_not_mistaken_for_a_host_and_port()
    {
        var normalized = HostSpec.Parse("fd00::1");

        Assert.Equal("fd00::1", normalized.Host);
        Assert.Null(normalized.Port);
    }

    [Fact]
    public void A_blank_host_stays_blank_so_the_caller_can_report_it()
    {
        Assert.Equal(string.Empty, HostSpec.Parse(null).Host);
        Assert.Equal(string.Empty, HostSpec.Parse("   ").Host);
    }
}
