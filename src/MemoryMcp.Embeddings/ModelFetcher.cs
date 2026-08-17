namespace MemoryMcp.Embeddings;

/// <summary>
/// Downloads the embedding model into a directory (MEMP-196).
/// <para>The model is not baked into the add-on image: the runtime is ~25 MB but the model is far larger, and
/// someone who never enables semantic recall should not carry it. That leaves the question of how it gets
/// there — and "shell into the container and run a command" is not an answer for an add-on, so the server
/// fetches it itself the first time the feature is switched on.</para>
/// </summary>
public static class ModelFetcher
{
    private const string BaseUrl = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/";

    /// <summary>The files the embedder needs, as (remote path, local name).</summary>
    private static readonly (string Remote, string Local)[] Files =
    [
        ("onnx/model.onnx", E5OnnxEmbedder.ModelFile),
        ("sentencepiece.bpe.model", E5OnnxEmbedder.TokenizerFile),
    ];

    /// <summary>True when both model files are already present, so a fetch would be wasted.</summary>
    /// <param name="directory">Model directory.</param>
    public static bool IsPresent(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && Files.All(file => File.Exists(Path.Combine(directory!, file.Local)));

    /// <summary>
    /// Downloads any missing file into <paramref name="directory"/>. Returns true when everything is present
    /// afterwards. Never throws for an unreachable host or a bad path — a failed fetch means the server keeps
    /// working lexically, which is far better than refusing to run.
    /// </summary>
    /// <param name="directory">Where to put the files.</param>
    /// <param name="report">Progress/diagnostic sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<bool> EnsureAsync(string? directory, Action<string> report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(directory))
        {
            report("No model directory configured (embedding_model_dir).");
            return false;
        }

        if (IsPresent(directory))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(directory!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            foreach (var (remote, local) in Files)
            {
                var target = Path.Combine(directory!, local);
                if (File.Exists(target))
                {
                    continue;
                }

                report($"Downloading {remote}...");
                using var response = await http.GetAsync(BaseUrl + remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    report($"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}. The host may be unreachable from this network.");
                    return false;
                }

                // Written under a temporary name so an interrupted download is never mistaken for a valid model.
                var partial = target + ".partial";
                await using (var file = File.Create(partial))
                {
                    await response.Content.CopyToAsync(file, cancellationToken);
                }

                File.Move(partial, target, overwrite: true);
                report($"  {new FileInfo(target).Length / 1048576.0:F1} MB");
            }

            return IsPresent(directory);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            report($"Download failed: {exception.Message}");
            return false;
        }
    }
}
