using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// The projector used by a type that declares no retrieval mapping (MEMP-251): index title, body, every tag
/// value and every payload string value — which is exactly what the server did before the seam existed.
/// <para>It is named <c>legacy</c> rather than <c>default</c> on purpose. Indexing every string is the right
/// default for full-text search and a poor one for embeddings: identifiers, URLs, status tokens and diagnostic
/// strings all become part of a note's meaning. Types earn declared mappings in MEMP-252; until then this keeps
/// them working, and <see cref="RetrievalDescriptor.IsLegacy"/> makes it visible how many still rely on it.</para>
/// </summary>
public sealed class LegacyRetrievalProjector : IRetrievalProjector
{
    /// <summary>Mapping identity for every type without declared annotations.</summary>
    public const string MappingVersion = "legacy@1";

    private static readonly string LegacyHash = Hash(MappingVersion);

    /// <inheritdoc />
    public RetrievalDescriptor Describe(string type) =>
        new(type, SchemaVersion: 0, MappingVersion, LegacyHash, IsLegacy: true);

    /// <inheritdoc />
    public IReadOnlyCollection<string> CurrentMappingHashes { get; } = [LegacyHash];

    /// <inheritdoc />
    public IReadOnlyList<RetrievalText> Lexical(NoteContent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var texts = new List<RetrievalText>();
        Add(texts, "title", note.Title);
        Add(texts, "body", note.Body);
        // Order matters and is part of the contract: the stems sidecar is built by concatenating these, so a
        // reordering would silently rewrite every note's indexed text on the next write.
        CollectJson(note.TagsJson, "tags", texts);
        CollectJson(note.PayloadJson, "payload", texts);
        return texts;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A type that declares nothing puts its body in the primary lane and everything else in the secondary one.
    /// That is not a judgement about the payload — it is the conservative reading of "we were not told", and it
    /// reproduces today's index exactly while both lanes are weighted the same.
    /// </remarks>
    public LexicalLanes Lanes(NoteContent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var texts = Lexical(note);
        var secondary = texts
            .Where(text => text.Path.StartsWith("payload", StringComparison.Ordinal))
            .Select(text => text.Text);
        return new LexicalLanes(note.Body, Join(secondary));
    }

    internal static string? Join(IEnumerable<string> parts)
    {
        var joined = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return joined.Length == 0 ? null : joined;
    }

    /// <inheritdoc />
    public IReadOnlyList<RetrievalPassage> Passages(NoteContent note)
    {
        var texts = Lexical(note);
        if (texts.Count == 0)
        {
            return Array.Empty<RetrievalPassage>();
        }

        var passages = new List<RetrievalPassage>();
        var title = texts.FirstOrDefault(text => text.Path == "title");
        if (title is not null)
        {
            // The title stands alone as well as leading each window: it is the note's most distilled statement
            // of what it is about, and averaging it into the body was measured to lose exactly that (MEMP-242).
            passages.Add(new RetrievalPassage("title", 0, title.Text, [title.Path]));
        }

        var rest = texts.Where(text => text.Path != "title").ToList();
        if (rest.Count > 0)
        {
            PassageWindows.Add(passages, "text", title?.Text, rest);
        }

        return passages;
    }

    private static void Add(List<RetrievalText> texts, string path, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            texts.Add(new RetrievalText(path, text!));
        }
    }

    // Every string VALUE in a JSON document, with its path. Keys are never indexed as text (MEMP-152 removed
    // them, because searching "status" otherwise matched every note that HAS a status).
    private static void CollectJson(string? json, string root, List<RetrievalText> into)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement, root, into);
        }
        catch (JsonException)
        {
            // Malformed payload indexes as nothing rather than failing the write; validation is the writer's job.
        }
    }

    private static void Walk(JsonElement element, string path, List<RetrievalText> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                Add(into, path, element.GetString());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.{property.Name}", into);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, string.Create(CultureInfo.InvariantCulture, $"{path}[{index++}]"), into);
                }

                break;
            default:
                break;
        }
    }

    private static string Hash(string mapping) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mapping)))[..16].ToLowerInvariant();
}
