using MemoryMcp.Core.Retrieval;
using MemoryMcp.Embeddings;
using Xunit;
using Xunit.Abstractions;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-196: the embedder must agree with the reference implementation EXACTLY, not merely produce a
/// well-formed vector.
/// <para>The failure this guards against is a plausible wrong answer. <c>SentencePieceTokenizer</c> returns raw
/// sentencepiece ids while XLM-R's vocabulary shifts every real piece by one and uses its own sentinels; with
/// the shift missing the model still runs, still returns 384 well-formed floats, and every cosine still looks
/// reasonable. Retrieval is just quietly worse, and the tokeniser is the last place anyone would look. Only an
/// exact-value assertion catches that, which is why the expected ids are hard-coded here.</para>
/// <para><b>Requires the model files</b> (<c>MEMORY_EMBEDDING_MODEL_DIR</c>, or the local HuggingFace cache).
/// They are far too large to commit, so these tests report themselves as skipped when the model is absent —
/// which is correct for CI, where the vector layer is off and never loaded. Run them before releasing any
/// change that touches embedding.</para>
/// </summary>
public class EmbedderParityTests(ITestOutputHelper output)
{
    private const string Probe = "query: three phase electricity monitoring device";

    // Captured from the Python reference (tokenizers + onnxruntime) on 2026-08-17.
    private static readonly long[] ExpectedIds = [0, 41, 1294, 12, 17262, 93402, 39108, 2481, 97204, 75186, 2];
    private static readonly float[] ExpectedHead = [0.047129f, -0.020693f, -0.062268f, -0.052878f, 0.036032f];

    [Fact]
    public void Tokenisation_matches_the_reference_ids_exactly()
    {
        if (ModelDirectory() is not { } directory)
        {
            output.WriteLine("SKIPPED: no model directory (set MEMORY_EMBEDDING_MODEL_DIR).");
            return;
        }

        using var embedder = new E5OnnxEmbedder(directory);

        Assert.Equal(ExpectedIds, embedder.BuildIds(Probe));
    }

    [Fact]
    public void An_embedding_matches_the_reference_vector()
    {
        if (ModelDirectory() is not { } directory)
        {
            output.WriteLine("SKIPPED: no model directory (set MEMORY_EMBEDDING_MODEL_DIR).");
            return;
        }

        using var embedder = new E5OnnxEmbedder(directory);

        // The probe already carries the "query: " prefix the reference used, so embed it as a passage to avoid
        // prefixing twice — this asserts the maths, and the prefix behaviour is asserted separately below.
        var vector = Assert.Single(embedder.Embed([Probe[7..]], EmbeddingKind.Query));

        Assert.Equal(384, vector.Length);
        for (var i = 0; i < ExpectedHead.Length; i++)
        {
            Assert.True(Math.Abs(vector[i] - ExpectedHead[i]) < 1e-4,
                $"dimension {i}: expected {ExpectedHead[i]}, got {vector[i]}");
        }

        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        Assert.True(Math.Abs(norm - 1.0) < 1e-5, $"vectors must be L2-normalised so a dot product is the cosine (norm {norm})");
    }

    /// <summary>
    /// e5 is trained asymmetrically: the same words indexed and searched must not produce the same vector, or
    /// the prefixes are not reaching the model and a chunk of the model's quality is being left unused.
    /// </summary>
    [Fact]
    public void A_query_and_a_passage_embed_differently()
    {
        if (ModelDirectory() is not { } directory)
        {
            output.WriteLine("SKIPPED: no model directory (set MEMORY_EMBEDDING_MODEL_DIR).");
            return;
        }

        using var embedder = new E5OnnxEmbedder(directory);
        const string Text = "three phase electricity monitoring device";

        var asQuery = Assert.Single(embedder.Embed([Text], EmbeddingKind.Query));
        var asPassage = Assert.Single(embedder.Embed([Text], EmbeddingKind.Passage));

        var cosine = asQuery.Zip(asPassage, (a, b) => a * b).Sum();
        Assert.True(cosine < 0.9999f, "query and passage prefixes should produce different vectors");
        Assert.True(cosine > 0.5f, $"the same text should still be close in either role (cosine {cosine})");
    }

    // The configured directory, else the local HuggingFace snapshot if one happens to be present.
    private static string? ModelDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("MEMORY_EMBEDDING_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && HasModel(configured))
        {
            return configured;
        }

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "huggingface", "hub", "models--intfloat--multilingual-e5-small", "snapshots");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        return Directory.EnumerateDirectories(cache).FirstOrDefault(HasModel);
    }

    private static bool HasModel(string directory) =>
        File.Exists(Path.Combine(directory, E5OnnxEmbedder.TokenizerFile));
}
