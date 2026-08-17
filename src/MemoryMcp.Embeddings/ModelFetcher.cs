using System.Security.Cryptography;

namespace MemoryMcp.Embeddings;

/// <summary>One file the embedder needs, and how to verify what arrived.</summary>
/// <param name="Url">Where to fetch it.</param>
/// <param name="Local">Name to store it under.</param>
/// <param name="Sha256">Expected content hash, lower-case hex.</param>
internal sealed record ModelFile(string Url, string Local, string Sha256);

/// <summary>
/// Downloads the embedding model into a directory (MEMP-196), in one of two builds (MEMP-263).
/// <para>The model is not baked into the add-on image: the runtime is ~25 MB but the model is far larger, and
/// someone who never enables semantic recall should not carry it. That leaves the question of how it gets
/// there — and "shell into the container and run a command" is not an answer for an add-on, so the server
/// fetches it itself the first time the feature is switched on.</para>
/// <para><b>Two builds, and the default is the larger one on purpose.</b> The int8 build is a quarter of the
/// size and embeds about twice as fast, but it was measured to cost one query of the twelve-query golden set —
/// a paraphrase query crossing the top-10 line — and the queries it costs are exactly the cross-language ones
/// the vector layer exists for. The download is a one-time cost; the recall is permanent. So <c>full</c> is the
/// default and <c>quantized</c> is there for a box where the reading (see <c>memory_load</c>) says the size or
/// the index-build time is a real problem.</para>
/// </summary>
public static class ModelFetcher
{
    /// <summary>The default build: full precision, best measured recall.</summary>
    public const string FullVariant = "full";

    /// <summary>The int8 build: a quarter of the size, roughly twice as fast, one golden-set query worse.</summary>
    public const string QuantizedVariant = "quantized";

    private const string Upstream = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/";
    private const string Mirror = "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/";

    // The tokenizer is shared: the quantised build is the same model, so it segments text identically. Only
    // the weights differ.
    private static readonly ModelFile Tokenizer = new(
        Upstream + "sentencepiece.bpe.model", E5OnnxEmbedder.TokenizerFile,
        "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865");

    private static readonly ModelFile FullWeights = new(
        Upstream + "onnx/model.onnx", E5OnnxEmbedder.ModelFile,
        "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665");

    // The upstream repository publishes fp32 only; the int8 export lives in the widely-used Xenova mirror.
    private static readonly ModelFile QuantizedWeights = new(
        Mirror + "onnx/model_quantized.onnx", E5OnnxEmbedder.ModelFile,
        "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193");

    private static ModelFile[] FilesFor(string? variant) =>
        [string.Equals(variant, QuantizedVariant, StringComparison.OrdinalIgnoreCase) ? QuantizedWeights : FullWeights, Tokenizer];

    /// <summary>True when both model files are already present, so a fetch would be wasted.</summary>
    /// <param name="directory">Model directory.</param>
    public static bool IsPresent(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && FilesFor(null).All(file => File.Exists(Path.Combine(directory!, file.Local)));

    /// <summary>
    /// Downloads any missing file into <paramref name="directory"/>. Returns true when everything is present
    /// and verified afterwards. Never throws for an unreachable host or a bad path — a failed fetch means the
    /// server keeps working lexically, which is far better than refusing to run.
    /// </summary>
    /// <param name="directory">Where to put the files.</param>
    /// <param name="report">Progress/diagnostic sink.</param>
    /// <param name="variant">Which build to fetch: <c>full</c> (default) or <c>quantized</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<bool> EnsureAsync(
        string? directory, Action<string> report, string? variant = null, CancellationToken cancellationToken = default)
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
            foreach (var file in FilesFor(variant))
            {
                if (!await FetchAsync(http, directory!, file, report, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            return IsPresent(directory);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            report($"Download failed: {exception.Message}");
            return false;
        }
    }

    private static async Task<bool> FetchAsync(
        HttpClient http, string directory, ModelFile file, Action<string> report, CancellationToken cancellationToken)
    {
        var target = Path.Combine(directory, file.Local);
        if (File.Exists(target))
        {
            return true;
        }

        report($"Downloading {file.Local}...");
        using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            report($"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}. The host may be unreachable from this network.");
            return false;
        }

        // Written under a temporary name so an interrupted download is never mistaken for a valid model.
        var partial = target + ".partial";
        await using (var stream = File.Create(partial))
        {
            await response.Content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        // Verified before it is put in place. A truncated response that still looks like a complete file is the
        // failure this catches: it would otherwise load, produce plausible-looking vectors, and quietly poison
        // the whole index — the worst shape of failure available here, because nothing would report it.
        var actual = await HashAsync(partial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            report($"Download of {file.Local} is corrupt (sha256 {actual[..16]}..., expected {file.Sha256[..16]}...); discarded.");
            return false;
        }

        File.Move(partial, target, overwrite: true);
        report($"  {new FileInfo(target).Length / 1048576.0:F1} MB, verified");
        return true;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
