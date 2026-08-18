using System.Globalization;
using MemoryMcp.Core.Retrieval;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Embeddings;

/// <summary>
/// Re-lanes any type whose retrieval mapping has changed since its notes were last indexed (MEMP-262).
/// <para>Editing a type's <c>x-retrieval</c> annotations is meant to BE the procedure — no version bump, no
/// redeploy, and no command to remember. Leaving the last step to a CLI would repeat the mistake semantic
/// recall already made once, where switching the feature on left two invisible prerequisites behind.</para>
/// <para>Background and throttled, for the same reason as the embedding index: this box also runs Home
/// Assistant and a camera recorder, and a corpus-wide reindex has no business competing with them. It is also
/// almost always a no-op — the check is two small queries, and work only happens after a mapping actually
/// changed.</para>
/// </summary>
public sealed class LaneRebuildService(
    LaneRebuilder rebuilder, TimeProvider clock, ILogger<LaneRebuildService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the server answer its health probe first; nothing here is urgent.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        try
        {
            var stale = rebuilder.TypesNeedingRebuild();
            if (stale.Count == 0)
            {
                return;
            }

            logger.LogInformation(
                "Retrieval mapping changed for {Count} type(s) ({Types}); recomputing their search text in the background.",
                stale.Count, string.Join(", ", stale));

            var total = 0;
            foreach (var type in stale)
            {
                var changed = await Task.Run(
                    () => rebuilder.Rebuild(type, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), stoppingToken),
                    stoppingToken).ConfigureAwait(false);
                total += changed;
                logger.LogInformation("Re-laned {Type}: {Changed} note(s) updated.", type, changed);

                // A breath between types, so a large corpus does not monopolise the box.
                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
            }

            logger.LogInformation("Search text is up to date with every type's mapping ({Total} note(s) updated).", total);
        }
        catch (OperationCanceledException)
        {
            // Shutting down. The state table is only written after a type completes, so an interrupted run is
            // retried next start rather than mistaken for finished.
        }
        catch (Exception exception)
        {
            // A failure here must never take the server down: stale lanes mean slightly coarser search, which
            // is enormously better than not starting.
            logger.LogError(exception, "Could not refresh search text after a mapping change; search continues with the previous text.");
        }
    }
}
