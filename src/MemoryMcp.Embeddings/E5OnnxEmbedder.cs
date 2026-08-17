using MemoryMcp.Core.Retrieval;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MemoryMcp.Embeddings;

/// <summary>
/// Runs <c>multilingual-e5-small</c> in-process through ONNX Runtime (MEMP-196) — chosen on measurement, not
/// preference: on identical data it beat the cheaper static alternative 6/12 to 5/12 on the golden set, and by
/// far more per query (position 1 against 32 on the query that started this work).
/// <para>The model is loaded from a configured directory rather than baked into the add-on image, so a server
/// with the layer switched off carries none of it.</para>
/// </summary>
public sealed class E5OnnxEmbedder : IEmbedder, IDisposable
{
    /// <summary>File names expected in the model directory.</summary>
    public const string ModelFile = "model.onnx";

    /// <summary>The sentencepiece vocabulary that pairs with the model.</summary>
    public const string TokenizerFile = "sentencepiece.bpe.model";

    // XLM-R's own specials, which are NOT the sentencepiece file's. See BuildIds.
    private const int BeginningOfSentence = 0;
    private const int EndOfSentence = 2;
    private const int FairseqOffset = 1;

    private const int MaxTokens = 512;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly bool _needsTokenTypes;

    /// <summary>Loads the model and its tokenizer from <paramref name="modelDirectory"/>.</summary>
    /// <param name="modelDirectory">Directory holding <see cref="ModelFile"/> and <see cref="TokenizerFile"/>.</param>
    /// <param name="modelId">Identity recorded with every vector this produces.</param>
    public E5OnnxEmbedder(string modelDirectory, string modelId = "intfloat/multilingual-e5-small")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ModelId = modelId;
        _session = new InferenceSession(ResolveModel(modelDirectory));
        using var vocabulary = File.OpenRead(Path.Combine(modelDirectory, TokenizerFile));
        _tokenizer = SentencePieceTokenizer.Create(vocabulary, addBeginningOfSentence: false, addEndOfSentence: false);
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        Dimensions = _session.OutputMetadata.Values.First().Dimensions[^1];
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, EmbeddingKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var prefix = kind == EmbeddingKind.Query ? "query: " : "passage: ";
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(EmbedOne(prefix + text));
        }

        return vectors;
    }

    /// <summary>
    /// Token ids in the layout the model was trained on.
    /// <para><b>The one thing that must not be simplified.</b> <c>SentencePieceTokenizer</c> returns RAW
    /// sentencepiece ids, where <c>&lt;unk&gt;=0, &lt;s&gt;=1, &lt;/s&gt;=2</c>. XLM-R's vocabulary instead uses
    /// <c>&lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3</c> and shifts every real piece up by one. Skip
    /// the shift and the model still runs, still returns a well-formed vector, and every cosine still looks
    /// plausible — retrieval is simply quietly worse, and the tokenizer is the last place anyone would look.
    /// Verified to exact id parity and 4.4e-7 vector agreement with the reference implementation.</para>
    /// </summary>
    /// <param name="text">Text to encode, prefix included.</param>
    internal long[] BuildIds(string text)
    {
        var pieces = _tokenizer.EncodeToIds(text);
        var body = pieces.Count > MaxTokens - 2 ? pieces.Take(MaxTokens - 2) : pieces;
        var ids = new List<long> { BeginningOfSentence };
        ids.AddRange(body.Select(id => (long)(id + FairseqOffset)));
        ids.Add(EndOfSentence);
        return [.. ids];
    }

    // A HuggingFace snapshot keeps the graph under onnx/; a hand-assembled directory usually keeps it flat.
    // Accept both rather than making the deployer reshuffle files to match us.
    private static string ResolveModel(string directory)
    {
        var flat = Path.Combine(directory, ModelFile);
        var nested = Path.Combine(directory, "onnx", ModelFile);
        return File.Exists(flat) ? flat
            : File.Exists(nested) ? nested
            : throw new FileNotFoundException($"No {ModelFile} in '{directory}' or its onnx/ subdirectory.", flat);
    }

    private float[] EmbedOne(string text)
    {
        var ids = BuildIds(text);
        var length = ids.Length;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Ones(length), [1, length])),
        };
        if (_needsTokenTypes)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[length], [1, length])));
        }

        using var results = _session.Run(inputs);
        return Normalize(MeanPool(results[0].AsTensor<float>(), length));
    }

    // Mean over real tokens. Every token is real here (batches of one, no padding), so the attention mask is
    // all ones and the mean is a plain average.
    private float[] MeanPool(Tensor<float> hidden, int tokens)
    {
        var pooled = new float[Dimensions];
        for (var token = 0; token < tokens; token++)
        {
            for (var d = 0; d < Dimensions; d++)
            {
                pooled[d] += hidden[0, token, d];
            }
        }

        for (var d = 0; d < Dimensions; d++)
        {
            pooled[d] /= tokens;
        }

        return pooled;
    }

    // L2 normalise so a dot product IS the cosine and no ranking code has to remember to divide.
    private static float[] Normalize(float[] vector)
    {
        var sum = 0d;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= double.Epsilon)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    private static long[] Ones(int length)
    {
        var ones = new long[length];
        Array.Fill(ones, 1L);
        return ones;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
