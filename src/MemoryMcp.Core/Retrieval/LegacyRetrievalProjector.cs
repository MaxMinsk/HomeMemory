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

    /// <summary>Passage window in characters, and its overlap — measured against the golden set (MEMP-242).</summary>
    private const int PassageChars = 320;
    private const int PassageOverlap = 80;

    /// <summary>Below this a window carries no usable meaning and is dropped rather than embedded.</summary>
    private const int MinPassageChars = 40;

    private static readonly string LegacyHash = Hash(MappingVersion);

    /// <inheritdoc />
    public RetrievalDescriptor Describe(string type) =>
        new(type, SchemaVersion: 0, MappingVersion, LegacyHash, IsLegacy: true);

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
            AddWindows(passages, title?.Text, rest);
        }

        return passages;
    }

    // Overlapping windows over the note's non-title text, each prefixed with the title so a passage keeps its
    // subject. Paths are carried through so a hit can name the fields it came from.
    private static void AddWindows(List<RetrievalPassage> passages, string? title, IReadOnlyList<RetrievalText> rest)
    {
        var joined = new StringBuilder();
        var offsets = new List<(int Start, string Path)>();
        foreach (var text in rest)
        {
            offsets.Add((joined.Length, text.Path));
            joined.Append(text.Text.Replace('\n', ' ')).Append(' ');
        }

        var all = joined.ToString();
        var lead = string.IsNullOrWhiteSpace(title) ? string.Empty : title + ". ";
        var ordinal = 0;
        for (var start = 0; start < all.Length; start += PassageChars - PassageOverlap)
        {
            var window = all.Substring(start, Math.Min(PassageChars, all.Length - start)).Trim();
            // The length floor exists to drop a meaningless trailing remnant, not to skip short notes: a note
            // whose whole text is under the floor must still be embedded, or every brief note in the corpus
            // would be searchable by its title alone.
            if (window.Length == 0 || (window.Length < MinPassageChars && ordinal > 0))
            {
                continue;
            }

            var end = start + PassageChars;
            var paths = offsets.Where(offset => offset.Start < end).Select(offset => offset.Path).Distinct(StringComparer.Ordinal).ToList();
            passages.Add(new RetrievalPassage("text", ordinal++, lead + window, paths));
        }
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
