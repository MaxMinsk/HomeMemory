using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Mqtt;

/// <summary>
/// Says at startup, in one line, whether push publishing is on and where it is pointed (MEMP-267).
/// <para><b>Why this exists.</b> The sinks are registered only when configured, so a misconfigured one
/// registers NOTHING — no client, no logger, no message. "MQTT is switched on but no device appeared in Home
/// Assistant" then produces complete silence at every log level, including trace, because there is no
/// component left to do the logging. The absence of a thing cannot report itself, so this reports it.</para>
/// </summary>
public sealed class EventSinkReport(string message, bool isProblem, ILogger<EventSinkReport> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (isProblem)
        {
            logger.LogWarning("{Message}", message);
        }
        else
        {
            logger.LogInformation("{Message}", message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
