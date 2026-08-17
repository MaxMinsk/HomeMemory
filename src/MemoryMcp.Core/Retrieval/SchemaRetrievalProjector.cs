using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MemoryMcp.Core.Schemas;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// Projects a note using its type's declared retrieval mapping, falling back to the legacy all-strings
/// projector for any type that declares none (MEMP-252).
/// <para><b>What changes and what does not.</b> Only the SEMANTIC arm is schema-driven here: which fields are
/// worth embedding, and which passage each belongs to. The lexical arm still indexes every string exactly as
/// before, on purpose — it is the frozen control against which the vector arm is measured, and moving both at
/// once would make a difference in the golden set unattributable. The declared <c>lexical</c> roles are parsed,
/// hashed and reported now; the FTS lanes that consume them are MEMP-262.</para>
/// <para><b>Why selecting fields matters more than it sounds.</b> Roughly one note in nine has an empty body
/// and lives entirely in its payload, and most of those are recipes. For those notes the choice between "embed
/// every string" and "embed the ones that carry meaning" is not a refinement — it decides whether the note's
/// vector describes food or describes units, enum tokens and identifiers.</para>
/// </summary>
public sealed class SchemaRetrievalProjector : IRetrievalProjector
{
    private readonly SchemaRegistry _schemas;
    private readonly LegacyRetrievalProjector _legacy = new();
    private readonly ConcurrentDictionary<string, RetrievalMapping?> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the projector over a schema registry.</summary>
    /// <param name="schemas">Where type schemas and their annotations come from.</param>
    public SchemaRetrievalProjector(SchemaRegistry schemas) =>
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));

    /// <inheritdoc />
    public RetrievalDescriptor Describe(string type)
    {
        var mapping = MappingFor(type);
        return mapping is null
            ? _legacy.Describe(type)
            : new RetrievalDescriptor(type, mapping.SchemaVersion, mapping.Version, mapping.Hash, IsLegacy: false);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> CurrentMappingHashes
    {
        get
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal) { _legacy.Describe(string.Empty).MappingHash };
            foreach (var schema in _schemas.All)
            {
                if (MappingFor(schema.Type) is { } mapping)
                {
                    hashes.Add(mapping.Hash);
                }
            }

            return hashes;
        }
    }

    /// <summary>Types that have a declared mapping, and types still on the legacy projector — reported by <c>memory_capabilities</c>.</summary>
    public (int Mapped, int Legacy) MappingCoverage()
    {
        var mapped = 0;
        var legacy = 0;
        foreach (var schema in _schemas.All)
        {
            if (MappingFor(schema.Type) is null)
            {
                legacy++;
            }
            else
            {
                mapped++;
            }
        }

        return (mapped, legacy);
    }

    /// <inheritdoc />
    public IReadOnlyList<RetrievalText> Lexical(NoteContent note) => _legacy.Lexical(note);

    /// <inheritdoc />
    public IReadOnlyList<RetrievalPassage> Passages(NoteContent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var mapping = MappingFor(note.Type);
        if (mapping is null)
        {
            return _legacy.Passages(note);
        }

        var passages = new List<RetrievalPassage>();
        if (!string.IsNullOrWhiteSpace(note.Title))
        {
            // The title stands alone as well as leading each window: it is the note's most distilled statement
            // of what it is about, and averaging it into the body was measured to lose exactly that (MEMP-242).
            passages.Add(new RetrievalPassage("title", 0, note.Title!, ["title"]));
        }

        var grouped = GroupPayload(note.PayloadJson, mapping);
        foreach (var group in mapping.GroupOrder)
        {
            if (grouped.TryGetValue(group, out var parts) && parts.Count > 0)
            {
                PassageWindows.Add(passages, group, note.Title, parts);
            }
        }

        if (!string.IsNullOrWhiteSpace(note.Body))
        {
            PassageWindows.Add(passages, "body", note.Title, [new RetrievalText("body", note.Body!)]);
        }

        return passages;
    }

    // Tags are deliberately absent from the semantic passages of a mapped type. They are exact-match facets
    // that the filter path already handles precisely; embedded, they mostly restate the title and add a
    // near-duplicate vector per note. The three-arm measurement is what decides whether that was right.
    private static Dictionary<string, List<RetrievalText>> GroupPayload(string? payloadJson, RetrievalMapping mapping)
    {
        var grouped = new Dictionary<string, List<RetrievalText>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return grouped;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson!);
            Walk(document.RootElement, string.Empty, string.Empty, mapping, grouped);
        }
        catch (JsonException)
        {
            // A malformed payload embeds as nothing rather than failing the write; validation is the writer's job.
        }

        return grouped;
    }

    // Carries two paths at once: the canonical one (arrays collapsed) that the annotation is declared against,
    // and the actual one (with indices) that a hit reports back to the caller.
    private static void Walk(
        JsonElement element, string canonical, string actual, RetrievalMapping mapping,
        Dictionary<string, List<RetrievalText>> grouped)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text) && mapping.ForPath(canonical)?.SemanticGroup is { } group)
                {
                    if (!grouped.TryGetValue(group, out var parts))
                    {
                        grouped[group] = parts = [];
                    }

                    parts.Add(new RetrievalText("payload." + actual, text!));
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(
                        property.Value,
                        canonical.Length == 0 ? property.Name : $"{canonical}.{property.Name}",
                        actual.Length == 0 ? property.Name : $"{actual}.{property.Name}",
                        mapping, grouped);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(
                        item, canonical + "[]",
                        string.Create(CultureInfo.InvariantCulture, $"{actual}[{index++}]"),
                        mapping, grouped);
                }

                break;
            default:
                break;
        }
    }

    // Cached per type AND schema version, so republishing a schema with edited annotations takes effect without
    // a restart while a hot path still costs one dictionary lookup.
    private RetrievalMapping? MappingFor(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        var schema = _schemas.GetLatest(type);
        if (schema is null)
        {
            return null;
        }

        return _cache.GetOrAdd(
            string.Create(CultureInfo.InvariantCulture, $"{type}@{schema.Version}"),
            _ => Parse(schema));
    }

    // A malformed annotation must not take the server down or silently disable indexing for the type: it falls
    // back to legacy, which is always correct if coarse. schema_upsert is where a bad annotation is REJECTED,
    // loudly, at the point someone can still fix it.
    private static RetrievalMapping? Parse(SchemaDefinition schema)
    {
        try
        {
            return RetrievalMapping.FromSchema(schema.Type, schema.Version, schema.Json);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
