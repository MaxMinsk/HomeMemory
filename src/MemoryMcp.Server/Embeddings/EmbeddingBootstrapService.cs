using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Embeddings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Server.Embeddings;

/// <summary>
/// Gets semantic recall from "switched on in the options" to "actually working", without anyone opening a
/// terminal (MEMP-196).
/// <para>Switching the feature on used to leave two hidden steps — fetch the model, build the index — that
/// could only be run inside the container. For a Home Assistant add-on that is not a workflow, it is a trap.
/// This does both in the background after startup.</para>
/// <para><b>Background, never at startup.</b> The download is hundreds of megabytes and the first index pass
/// walks the whole corpus; doing either before the server listens would leave the add-on looking dead and
/// invite the watchdog to restart it mid-download, forever.</para>
/// </summary>
public sealed class EmbeddingBootstrapService(
    EmbeddingOptions options, VectorRecall vectors, PassageStore passages, NotesReader notes,
    IRetrievalProjector projector, ILogger<EmbeddingBootstrapService> logger) : BackgroundService
{
    /// <summary>Notes indexed per batch, so progress is visible and the loop can be cancelled promptly.</summary>
    private const int Batch = 200;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        // Let the server finish starting and answer its health probe before competing for the CPU.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        if (!ModelFetcher.IsPresent(options.ModelDirectory))
        {
            logger.LogInformation("Semantic recall is on but the model is missing; fetching it into {Directory}. Search stays lexical until it lands.", options.ModelDirectory);
            if (!await ModelFetcher.EnsureAsync(options.ModelDirectory, message => logger.LogInformation("{Message}", message), stoppingToken).ConfigureAwait(false))
            {
                logger.LogWarning("Could not fetch the embedding model; continuing with lexical search only.");
                return;
            }

            logger.LogInformation("Embedding model ready.");
        }

        // The model may have arrived after the first load attempt failed, so let the layer bind again.
        vectors.Reload();
        if (!vectors.Enabled)
        {
            logger.LogWarning("Embedding model present but could not be loaded; continuing with lexical search only.");
            return;
        }

        await BuildIndexAsync(stoppingToken).ConfigureAwait(false);
    }

    // Indexes notes that have no passage under the current model and mapping. Runs once per start; a note
    // written later indexes itself on write, and a model or mapping change makes the rest visible here again.
    private async Task BuildIndexAsync(CancellationToken stoppingToken)
    {
        var mappingHash = projector.Describe(string.Empty).MappingHash;
        var model = vectors.ModelId!;
        var done = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = passages.NeedingIndex(model, mappingHash, Batch);
            if (pending.Count == 0)
            {
                break;
            }

            if (done == 0)
            {
                logger.LogInformation("Building the embedding index; {Count} note(s) in the first batch.", pending.Count);
            }

            foreach (var id in pending)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (notes.Get(id) is { } note)
                {
                    vectors.Index(id, new NoteContent(note.Type, note.Title, note.Body, note.TagsJson, note.PayloadJson),
                        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                }

                done++;
                // A brief yield between notes: this box also runs Home Assistant and a camera recorder, and a
                // one-off backfill has no business starving them. Latency here costs nobody anything.
                await Task.Delay(TimeSpan.FromMilliseconds(20), stoppingToken).ConfigureAwait(false);
            }

            logger.LogInformation("Embedding index: {Done} note(s) done.", done);
        }

        if (done > 0)
        {
            logger.LogInformation("Embedding index complete: {Done} note(s). Semantic recall is live.", done);
        }
    }
}
