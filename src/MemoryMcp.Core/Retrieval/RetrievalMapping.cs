using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryMcp.Core.Retrieval;

/// <summary>How a field contributes to the lexical (full-text) index.</summary>
public enum LexicalRole
{
    /// <summary>Not indexed as text.</summary>
    None,

    /// <summary>Carries the field's subject — weighted above ordinary text.</summary>
    Primary,

    /// <summary>Findable, but not what the note is about.</summary>
    Secondary,
}

/// <summary>
/// What retrieval does with one field of a type (MEMP-252).
/// </summary>
/// <param name="Path">Canonical path within the payload, arrays collapsed: <c>ingredients[].name</c>.</param>
/// <param name="Lexical">Its part in the full-text index.</param>
/// <param name="SemanticGroup">The passage group it is embedded into, or null to never embed it.</param>
/// <param name="Role">A universal signal this field supplies (<c>observed_at</c>, <c>valid_to</c>, <c>importance</c>).</param>
public sealed record FieldRetrieval(string Path, LexicalRole Lexical, string? SemanticGroup, string? Role);

/// <summary>
/// A type's declared retrieval mapping — the second of the three layers (MEMP-252): the data schema says what a
/// note IS, this says how it is found, and the ranking profile says how competing hits are ordered.
/// <para><b>Why annotations rather than a separate document.</b> A mapping that lives apart from the schema
/// drifts from it: a renamed field leaves a mapping pointing at nothing, and nothing fails. Written as
/// <c>x-retrieval</c> on the property itself, the two cannot disagree — and JSON Schema validators ignore
/// unknown keywords, so declaring them changes no validation behaviour whatsoever.</para>
/// <para><b>Why the hash is separate from the schema version.</b> Editing an annotation is not a data-contract
/// change: no stored note becomes invalid, so it must NOT bump <c>fact@1</c> to <c>fact@2</c>. What it does
/// invalidate is the index, and that is exactly what the mapping hash dates.</para>
/// </summary>
public sealed class RetrievalMapping
{
    /// <summary>The annotation keyword, on the schema root and on individual properties.</summary>
    public const string Keyword = "x-retrieval";

    /// <summary>Bounds for the type-level recency prior, clamped rather than trusted (MEMP-252).</summary>
    public const double MinHalfLifeDays = 0.5;

    /// <summary>Ten years — beyond this a decay is indistinguishable from none, so the extra range buys nothing.</summary>
    public const double MaxHalfLifeDays = 3650d;

    private readonly Dictionary<string, FieldRetrieval> _byPath;

    private RetrievalMapping(
        string type, int schemaVersion, string version, string? typeClass, double? halfLifeDays,
        IReadOnlyList<FieldRetrieval> fields, IReadOnlyList<string> groupOrder)
    {
        Type = type;
        SchemaVersion = schemaVersion;
        Version = version;
        TypeClass = typeClass;
        HalfLifeDays = halfLifeDays;
        Fields = fields;
        GroupOrder = groupOrder;
        _byPath = fields.ToDictionary(field => field.Path, StringComparer.Ordinal);
        Hash = ComputeHash(this);
    }

    /// <summary>The note type this maps.</summary>
    public string Type { get; }

    /// <summary>The data schema version the mapping was read from.</summary>
    public int SchemaVersion { get; }

    /// <summary>Mapping identity, e.g. <c>fact@1/r1</c>.</summary>
    public string Version { get; }

    /// <summary>Type class prior (canonical, episodic, ...), consumed by ranking in MEMP-253.</summary>
    public string? TypeClass { get; }

    /// <summary>Recency half-life prior in days, already clamped. Consumed by ranking in MEMP-253.</summary>
    public double? HalfLifeDays { get; }

    /// <summary>Every annotated field.</summary>
    public IReadOnlyList<FieldRetrieval> Fields { get; }

    /// <summary>Passage groups in schema declaration order, so passage ordinals are stable across rebuilds.</summary>
    public IReadOnlyList<string> GroupOrder { get; }

    /// <summary>Identity of this mapping's content — what dates a stored passage.</summary>
    public string Hash { get; }

    /// <summary>The rule for a payload path, or null when the field is unannotated.</summary>
    /// <param name="path">Canonical path, arrays already collapsed.</param>
    public FieldRetrieval? ForPath(string path)
    {
        if (_byPath.TryGetValue(path, out var exact))
        {
            return exact;
        }

        // An array of plain strings carries its annotation on the array itself — there is no sub-schema to put
        // it on — while its VALUES arrive one level deeper, at "<path>[]". Arrays of objects need no such help:
        // their annotations sit on the item properties and already match ("ingredients[].name").
        return path.EndsWith("[]", StringComparison.Ordinal)
            ? _byPath.GetValueOrDefault(path[..^2])
            : null;
    }

    /// <summary>
    /// Reads a type's mapping out of its schema JSON, or returns null when it declares no annotations at all —
    /// which is the signal to keep using the legacy all-strings projector for that type.
    /// </summary>
    /// <param name="type">Note type.</param>
    /// <param name="schemaVersion">Schema version.</param>
    /// <param name="schemaJson">The schema document.</param>
    /// <exception cref="ArgumentException">The annotations are present but malformed.</exception>
    public static RetrievalMapping? FromSchema(string type, int schemaVersion, string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(schemaJson!);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var fields = new List<FieldRetrieval>();
            var groups = new List<string>();
            if (root.TryGetProperty("properties", out var properties))
            {
                Collect(properties, string.Empty, fields, groups);
            }

            var hasTypeLevel = root.TryGetProperty(Keyword, out var typeLevel);
            if (fields.Count == 0 && !hasTypeLevel)
            {
                return null;
            }

            var (version, typeClass, halfLife) = ReadTypeLevel(type, hasTypeLevel ? typeLevel : default, hasTypeLevel);
            return new RetrievalMapping(type, schemaVersion, version, typeClass, halfLife, fields, groups);
        }
    }

    // Type-level priors are validated and clamped here rather than trusted, because they arrive from
    // schema_upsert — an agent-writable surface. An out-of-range half-life is a mistake worth naming, not
    // silently honouring: a 0-day half-life would erase every note from ranking the moment it was written.
    private static (string Version, string? TypeClass, double? HalfLifeDays) ReadTypeLevel(
        string type, JsonElement element, bool present)
    {
        var version = "r1";
        string? typeClass = null;
        double? halfLife = null;

        if (present && element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("version", out var declared) && declared.ValueKind == JsonValueKind.String)
            {
                version = declared.GetString() ?? version;
            }

            if (element.TryGetProperty("class", out var declaredClass) && declaredClass.ValueKind == JsonValueKind.String)
            {
                typeClass = declaredClass.GetString();
            }

            if (element.TryGetProperty("halfLifeDays", out var days) && days.ValueKind == JsonValueKind.Number)
            {
                var value = days.GetDouble();
                if (value < MinHalfLifeDays || value > MaxHalfLifeDays)
                {
                    throw new ArgumentException(
                        $"{type}: x-retrieval.halfLifeDays must be between {MinHalfLifeDays} and {MaxHalfLifeDays}, got {value}.",
                        nameof(type));
                }

                halfLife = value;
            }
        }

        return ($"{type}@_v/{version}".Replace("@_v", string.Empty, StringComparison.Ordinal), typeClass, halfLife);
    }

    // Walks the schema shape so a declared path matches the payload path a projector will produce: an array
    // contributes "[]" and an object contributes ".name". Annotations therefore cannot name a field that does
    // not exist — the walk only ever visits declared properties.
    private static void Collect(JsonElement properties, string prefix, List<FieldRetrieval> fields, List<string> groups)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            var schema = property.Value;
            if (schema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (schema.TryGetProperty(Keyword, out var annotation))
            {
                var field = ReadField(path, annotation);
                fields.Add(field);
                if (field.SemanticGroup is { } group && !groups.Contains(group, StringComparer.Ordinal))
                {
                    groups.Add(group);
                }
            }

            if (schema.TryGetProperty("properties", out var nested))
            {
                Collect(nested, path, fields, groups);
            }

            if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("properties", out var itemProperties))
            {
                Collect(itemProperties, path + "[]", fields, groups);
            }
        }
    }

    private static FieldRetrieval ReadField(string path, JsonElement annotation)
    {
        if (annotation.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{path}: {Keyword} must be an object.", nameof(path));
        }

        var lexical = LexicalRole.None;
        if (annotation.TryGetProperty("lexical", out var declared) && declared.ValueKind == JsonValueKind.String)
        {
            lexical = declared.GetString() switch
            {
                "primary" => LexicalRole.Primary,
                "secondary" => LexicalRole.Secondary,
                "none" => LexicalRole.None,
                var other => throw new ArgumentException(
                    $"{path}: {Keyword}.lexical must be primary, secondary or none, got '{other}'.", nameof(path)),
            };
        }

        // `semantic` names the passage group directly: a string means "embed it into that group", and its
        // absence means "never embed this". One key rather than two, so a field cannot declare itself embedded
        // and then fail to say where.
        string? group = null;
        if (annotation.TryGetProperty("semantic", out var semantic))
        {
            group = semantic.ValueKind switch
            {
                JsonValueKind.String => semantic.GetString(),
                JsonValueKind.False => null,
                _ => throw new ArgumentException(
                    $"{path}: {Keyword}.semantic must be a passage group name or false.", nameof(path)),
            };
        }

        string? role = null;
        if (annotation.TryGetProperty("role", out var declaredRole) && declaredRole.ValueKind == JsonValueKind.String)
        {
            role = declaredRole.GetString();
        }

        return new FieldRetrieval(path, lexical, string.IsNullOrWhiteSpace(group) ? null : group, role);
    }

    // Covers everything that could change what gets indexed, so an edit to any of it marks the affected
    // vectors stale. It deliberately also covers the lexical roles, which do not yet drive the FTS columns
    // (MEMP-262): an occasional needless reindex is a far cheaper mistake than serving a vector built by a
    // mapping nobody can reconstruct.
    private static string ComputeHash(RetrievalMapping mapping)
    {
        var builder = new StringBuilder()
            .Append(mapping.Type).Append('|')
            .Append(mapping.Version).Append('|')
            .Append(mapping.TypeClass).Append('|')
            .Append(mapping.HalfLifeDays?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        foreach (var field in mapping.Fields.OrderBy(field => field.Path, StringComparer.Ordinal))
        {
            builder.Append(field.Path).Append(':')
                .Append(field.Lexical).Append(':')
                .Append(field.SemanticGroup).Append(':')
                .Append(field.Role).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16].ToLowerInvariant();
    }
}
