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
        string type, int schemaVersion, string version, Schemas.TypeTraits? traits,
        IReadOnlyList<FieldRetrieval> fields, IReadOnlyList<string> groupOrder)
    {
        Type = type;
        SchemaVersion = schemaVersion;
        Version = version;
        Traits = traits;
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

    /// <summary>
    /// The type's declared behaviour in ranking, ageing and lint (MEMP-253), or null when it declares none —
    /// which is the signal to fall back to the bridge rather than to silently apply the defaults.
    /// </summary>
    public Schemas.TypeTraits? Traits { get; }

    /// <summary>Type class prior (canonical, episodic, ...), consumed by ranking.</summary>
    public string? TypeClass => Traits?.Class;

    /// <summary>Recency half-life prior in days, already validated. Consumed by ranking.</summary>
    public double? HalfLifeDays => Traits?.HalfLifeDays;

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
                Collect(properties, string.Empty, fields, groups, root, 0);
            }

            var hasTypeLevel = root.TryGetProperty(Keyword, out var typeLevel);
            if (fields.Count == 0 && !hasTypeLevel)
            {
                return null;
            }

            var (version, traits) = ReadTypeLevel(type, hasTypeLevel ? typeLevel : default, hasTypeLevel);
            return new RetrievalMapping(type, schemaVersion, version, traits, fields, groups);
        }
    }

    // Type-level priors are validated and clamped here rather than trusted, because they arrive from
    // schema_upsert — an agent-writable surface. An out-of-range half-life is a mistake worth naming, not
    // silently honouring: a 0-day half-life would erase every note from ranking the moment it was written.
    private static (string Version, Schemas.TypeTraits? Traits) ReadTypeLevel(
        string type, JsonElement element, bool present)
    {
        var version = "r1";
        if (!present || element.ValueKind != JsonValueKind.Object)
        {
            return (version, null);
        }

        if (element.TryGetProperty("version", out var declared) && declared.ValueKind == JsonValueKind.String)
        {
            version = declared.GetString() ?? version;
        }

        var defaults = Schemas.TypeTraits.Default;
        var traits = new Schemas.TypeTraits(
            Class: Text(element, "class") ?? defaults.Class,
            HalfLifeDays: HalfLife(type, element) ?? defaults.HalfLifeDays,
            ExpectsTags: Flag(element, "expectsTags") ?? defaults.ExpectsTags,
            ExpectsLinks: Flag(element, "expectsLinks") ?? defaults.ExpectsLinks,
            ClaimLike: Flag(element, "claimLike") ?? defaults.ClaimLike);

        if (!KnownClasses.Contains(traits.Class, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"{type}: {Keyword}.class must be one of {string.Join(", ", KnownClasses)}, got '{traits.Class}'.",
                nameof(type));
        }

        return (version, traits);
    }

    /// <summary>The ranking classes a type may declare; anything else is a typo, not a new policy.</summary>
    private static readonly string[] KnownClasses = ["canonical", "workflow", "episodic", "ordinary"];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    // Validated and REJECTED rather than clamped: these arrive through schema_upsert, an agent-writable
    // surface, and a half-life of zero would erase every note of the type from ranking the instant it was
    // written. A silent clamp would hide the mistake; the author is the one who can fix it.
    private static double? HalfLife(string type, JsonElement element)
    {
        if (!element.TryGetProperty("halfLifeDays", out var days) || days.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var value = days.GetDouble();
        if (value < MinHalfLifeDays || value > MaxHalfLifeDays)
        {
            throw new ArgumentException(
                $"{type}: {Keyword}.halfLifeDays must be between {MinHalfLifeDays} and {MaxHalfLifeDays}, got {value}.",
                nameof(type));
        }

        return value;
    }

    /// <summary>
    /// Guards against a <c>$ref</c> cycle, which a schema is free to contain (a step whose sub-steps are steps).
    /// A depth cap rather than a visited-set because the same definition legitimately appears at several paths.
    /// </summary>
    private const int MaxDepth = 12;

    // Walks the schema shape so a declared path matches the payload path a projector will produce: an array
    // contributes "[]" and an object contributes ".name". Annotations therefore cannot name a field that does
    // not exist — the walk only ever visits declared properties.
    private static void Collect(
        JsonElement properties, string prefix, List<FieldRetrieval> fields, List<string> groups,
        JsonElement root, int depth)
    {
        if (properties.ValueKind != JsonValueKind.Object || depth > MaxDepth)
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            var schema = Resolve(property.Value, root, depth);
            if (schema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // The annotation is read from the property, NOT from the resolved target: two properties can share
            // one $defs entry and still want different retrieval treatment, and a shared definition must not
            // acquire an annotation meant for one of its users.
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty(Keyword, out var annotation))
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
                Collect(nested, path, fields, groups, root, depth + 1);
            }

            if (schema.TryGetProperty("items", out var items))
            {
                var resolvedItems = Resolve(items, root, depth);
                if (resolvedItems.ValueKind == JsonValueKind.Object
                    && resolvedItems.TryGetProperty("properties", out var itemProperties))
                {
                    Collect(itemProperties, path + "[]", fields, groups, root, depth + 1);
                }
            }
        }
    }

    // Follows a local "#/$defs/name" pointer. Only same-document refs are followed: a remote ref would mean a
    // fetch on a hot path, and the schemas this serves are self-contained by construction. An unresolvable ref
    // yields the ref node itself, which simply contributes no annotated fields rather than throwing — the
    // validator is what judges whether a schema is well-formed.
    private static JsonElement Resolve(JsonElement schema, JsonElement root, int depth)
    {
        var current = schema;
        for (var hop = 0; hop < MaxDepth - depth && current.ValueKind == JsonValueKind.Object; hop++)
        {
            if (!current.TryGetProperty("$ref", out var reference) || reference.ValueKind != JsonValueKind.String)
            {
                return current;
            }

            var pointer = reference.GetString();
            if (pointer is null || !pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                return current;
            }

            var target = root;
            foreach (var segment in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                // JSON Pointer escapes, in the order the specification requires: ~1 before ~0.
                var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(name, out target))
                {
                    return current;
                }
            }

            current = target;
        }

        return current;
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
            .Append(mapping.Traits).Append('|');
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
