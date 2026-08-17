using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Schemas;

/// <summary>
/// Resolves payload JSON Schemas for note types. Two-tier (MEMP-060): <b>built-in</b> schemas are
/// loaded from embedded resources (code-owned, read-only) and seeded into the <c>schemas</c> table;
/// <b>agent-authored</b> schemas live only in that table and can be added/updated at runtime via
/// <see cref="Upsert"/>. Built-ins always win on a type-name clash. Thread-safe for concurrent reads
/// and runtime authoring.
/// </summary>
public sealed class SchemaRegistry
{
    private readonly Dictionary<string, SchemaDefinition> _byTypeVersion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SchemaDefinition> _latestByType = new(StringComparer.Ordinal);
    private readonly HashSet<string> _builtinTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _builtinVersions = new(StringComparer.Ordinal); // "type@version" of code-owned schemas
    private readonly object _gate = new();
    private int _generation;

    /// <summary>
    /// Bumped whenever a schema is registered or re-registered. Anything caching something DERIVED from a
    /// schema — a retrieval mapping, a type policy — must watch this rather than the type version.
    /// <para>Version is not enough on its own: an annotation-only edit deliberately keeps the same version
    /// (that is the whole point of MEMP-252's immutability exclusion), so a cache keyed by <c>type@version</c>
    /// keeps serving the answer from before the edit. Retrieval could then only be retuned by restarting the
    /// server, which is exactly what the exclusion existed to avoid.</para>
    /// </summary>
    public int Generation
    {
        get { lock (_gate) { return _generation; } }
    }

    private SchemaRegistry(IEnumerable<SchemaDefinition> builtins)
    {
        foreach (var definition in builtins)
        {
            _builtinTypes.Add(definition.Type);
            _builtinVersions.Add(Key(definition.Type, definition.Version));
            Index(definition);
        }
    }

    /// <summary>All registered schema definitions (built-in + agent-authored).</summary>
    public IReadOnlyCollection<SchemaDefinition> All
    {
        get { lock (_gate) { return _byTypeVersion.Values.ToList(); } }
    }

    /// <summary>True if the type is a code-owned built-in (read-only to authoring).</summary>
    public bool IsBuiltin(string type) => _builtinTypes.Contains(type);

    /// <summary>Builds a registry from the JSON Schemas embedded in the given assembly (defaults to Core).</summary>
    /// <param name="assembly">Assembly to scan; defaults to the one declaring this type.</param>
    public static SchemaRegistry FromEmbeddedResources(Assembly? assembly = null)
    {
        assembly ??= typeof(SchemaRegistry).Assembly;
        RegisterTraits(assembly);
        var definitions = new List<SchemaDefinition>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Schemas.", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.Ordinal) ||
                name.Contains(".Schemas.Traits.", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded schema '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var (type, version) = ParseId(json, name);
            definitions.Add(new SchemaDefinition(type, version, json, JsonSchema.FromText(json)));
        }

        return new SchemaRegistry(definitions);
    }

    /// <summary>
    /// Makes the shared trait schemas resolvable, so a type can compose one with <c>allOf</c> + <c>$ref</c>
    /// (MEMP-268).
    /// <para>Traits are schema FRAGMENTS, not note types: they declare a concept several types share — universal
    /// ranking signals, temporal validity — in one place, so it does not have to be restated (and drift) per
    /// type. They are registered with the JSON Schema resolver and deliberately NOT added as note types: nobody
    /// writes a note whose type is "rankable".</para>
    /// <para>A trait's <c>$id</c> pins its version (<c>rankable@1</c>, never "latest"), because a type that
    /// composes it has validated its stored notes against exactly that shape. Redefining a published trait
    /// underneath its users is the one change this layer must never allow.</para>
    /// </summary>
    private static void RegisterTraits(Assembly assembly)
    {
        // Once per assembly, under a lock. The JSON Schema resolver's registry is process-global and shared
        // mutable state; registering into it from every registry construction raced as soon as two were built
        // concurrently, which surfaced as an unrelated test failing intermittently.
        lock (TraitGate)
        {
            if (!TraitAssemblies.Add(assembly.FullName ?? assembly.ToString()))
            {
                return;
            }

            RegisterTraitsCore(assembly);
        }
    }

    private static readonly object TraitGate = new();
    private static readonly HashSet<string> TraitAssemblies = new(StringComparer.Ordinal);

    private static void RegisterTraitsCore(Assembly assembly)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Schemas.Traits.", StringComparison.Ordinal)
                || !name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded trait '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            var trait = JsonSchema.FromText(reader.ReadToEnd());
            // Idempotent: registering the same $id twice is how a second registry instance in the same process
            // (every test does this) reuses what is already resolvable.
            Json.Schema.SchemaRegistry.Global.Register(trait);
        }
    }

    /// <summary>Returns the latest registered version for <paramref name="type"/>, or <c>null</c>.</summary>
    public SchemaDefinition? GetLatest(string type)
    {
        lock (_gate)
        {
            return _latestByType.TryGetValue(type, out var definition) ? definition : null;
        }
    }

    /// <summary>Returns a specific <paramref name="type"/> at <paramref name="version"/>, or <c>null</c>.</summary>
    public SchemaDefinition? Get(string type, int version)
    {
        lock (_gate)
        {
            return _byTypeVersion.TryGetValue(Key(type, version), out var definition) ? definition : null;
        }
    }

    /// <summary>Loads agent-authored schemas (non-built-in types) from the database into the registry.
    /// Called once at startup, after <see cref="SyncToDatabase"/> has seeded the built-ins.</summary>
    /// <param name="connectionFactory">Factory for the database to read from.</param>
    public void LoadFromDatabase(ISqliteConnectionFactory connectionFactory)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, version, json_schema FROM schemas;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var version = reader.GetInt32(1);
            if (_builtinVersions.Contains(Key(type, version)))
            {
                continue; // shipped built-in versions come from embedded resources, which are authoritative
            }

            var definition = new SchemaDefinition(type, version, reader.GetString(2), JsonSchema.FromText(reader.GetString(2)));
            lock (_gate)
            {
                Index(definition);
            }
        }
    }

    /// <summary>
    /// Adds or updates an agent-authored schema at runtime. The document must be a valid JSON Schema
    /// whose <c>$id</c> is <c>type@version</c>. Built-in types are read-only; a version already used by
    /// existing notes cannot be changed (bump the version instead).
    /// </summary>
    /// <param name="connectionFactory">Database to persist to and to check note usage against.</param>
    /// <param name="json">The JSON Schema document.</param>
    /// <param name="author">Who is authoring (provenance, recorded for audit; optional).</param>
    /// <returns>The stored definition.</returns>
    public SchemaDefinition Upsert(ISqliteConnectionFactory connectionFactory, string json, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new SchemaAuthoringException("Schema document is empty.");
        }

        string type;
        int version;
        try
        {
            (type, version) = ParseId(json, "(upsert)");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new SchemaAuthoringException(exception.Message);
        }

        if (_builtinVersions.Contains(Key(type, version)))
        {
            throw new SchemaAuthoringException(
                $"Schema '{type}@{version}' is a built-in (shipped) version and read-only. Author a higher version (e.g. {type}@{version + 1}) to evolve it.");
        }

        JsonSchema compiled;
        try
        {
            compiled = JsonSchema.FromText(json);
        }
        catch (Exception exception)
        {
            throw new SchemaAuthoringException($"Not a valid JSON Schema: {exception.Message}");
        }

        ValidateAnnotations(type, version, json);
        ValidateReferences(type, version, compiled);

        var existing = Get(type, version);
        if (existing is not null && !string.Equals(existing.Json, json, StringComparison.Ordinal)
            && !OnlyRetrievalAnnotationsChanged(existing.Json, json) && NotesExist(connectionFactory, type))
        {
            throw new SchemaAuthoringException(
                $"Schema '{type}@{version}' is already in use by existing notes; bump the version for changes.");
        }

        var definition = new SchemaDefinition(type, version, json, compiled);
        Persist(connectionFactory, definition, author);
        lock (_gate)
        {
            Index(definition);
        }

        return definition;
    }

    /// <summary>Schema authoring provenance for audit (MEMP-122): every registered schema with its author and last-write time.</summary>
    /// <param name="connectionFactory">Database to read from.</param>
    public static IReadOnlyList<SchemaProvenance> Provenance(ISqliteConnectionFactory connectionFactory)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, version, author, updated_utc FROM schemas ORDER BY type, version;";

        var list = new List<SchemaProvenance>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SchemaProvenance(
                reader.GetString(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return list;
    }

    /// <summary>Idempotently upserts every built-in schema into the <c>schemas</c> table.</summary>
    /// <param name="connectionFactory">Factory for the database to write to.</param>
    public void SyncToDatabase(ISqliteConnectionFactory connectionFactory)
    {
        using var connection = connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        foreach (var definition in All)
        {
            if (!_builtinVersions.Contains(Key(definition.Type, definition.Version)))
            {
                continue; // only sync shipped built-in versions; agent-authored ones persist via Upsert
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO schemas (type, version, json_schema, author, updated_utc) " +
                "VALUES ($t, $v, $j, 'system', strftime('%Y-%m-%dT%H:%M:%fZ','now')) " +
                "ON CONFLICT(type, version) DO UPDATE SET json_schema = excluded.json_schema, author = 'system';";
            command.Parameters.AddWithValue("$t", definition.Type);
            command.Parameters.AddWithValue("$v", definition.Version);
            command.Parameters.AddWithValue("$j", definition.Json);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Persist(ISqliteConnectionFactory connectionFactory, SchemaDefinition definition, string? author)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO schemas (type, version, json_schema, author, updated_utc) " +
            "VALUES ($t, $v, $j, $a, strftime('%Y-%m-%dT%H:%M:%fZ','now')) " +
            "ON CONFLICT(type, version) DO UPDATE SET json_schema = excluded.json_schema, " +
            "author = excluded.author, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$t", definition.Type);
        command.Parameters.AddWithValue("$v", definition.Version);
        command.Parameters.AddWithValue("$j", definition.Json);
        command.Parameters.AddWithValue("$a", (object?)author ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // A $ref that resolves to nothing compiles fine and only fails when a note is validated against it — by
    // which point the author is gone and the error surfaces as a rejected WRITE on an unrelated note. Resolving
    // it once here, against a trivial instance, moves the failure to the moment it can still be fixed.
    private static void ValidateReferences(string type, int version, JsonSchema compiled)
    {
        try
        {
            compiled.Evaluate(new JsonObject(), new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new SchemaAuthoringException(
                $"Schema '{type}@{version}' has a reference that cannot be resolved: {exception.Message}. "
                + "A $ref must point at a registered trait (pinned to its version, e.g. memory:trait/rankable@1) "
                + "or at a local $defs entry.");
        }
    }

    // Retrieval annotations are validated HERE, where an author can still fix them. The projector's own
    // fallback (legacy indexing on a parse failure) keeps the server running but is silent — a typo'd
    // annotation would simply stop taking effect, and nothing would ever say so.
    private static void ValidateAnnotations(string type, int version, string json)
    {
        try
        {
            Retrieval.RetrievalMapping.FromSchema(type, version, json);
        }
        catch (ArgumentException exception)
        {
            throw new SchemaAuthoringException($"Invalid {Retrieval.RetrievalMapping.Keyword} annotation: {exception.Message}");
        }
    }

    /// <summary>
    /// True when two versions of a schema differ ONLY in their <c>x-retrieval</c> annotations (MEMP-252).
    /// <para>This is what lets retrieval be retuned without a data-contract version bump. The immutability rule
    /// exists because changing a published type can invalidate stored notes — but an annotation cannot: JSON
    /// Schema validators ignore unknown keywords, so by construction every note that validated before still
    /// validates. What an annotation change DOES invalidate is the index, and that is tracked separately by the
    /// mapping hash, which marks the affected vectors stale and schedules a selective reindex.</para>
    /// <para>Comparison is structural, not textual: the two documents are re-serialised with every
    /// <c>x-retrieval</c> node removed, so reformatting alone will not slip a real change past this.</para>
    /// </summary>
    private static bool OnlyRetrievalAnnotationsChanged(string existing, string candidate)
    {
        try
        {
            using var before = System.Text.Json.JsonDocument.Parse(existing);
            using var after = System.Text.Json.JsonDocument.Parse(candidate);
            return string.Equals(
                WithoutAnnotations(before.RootElement), WithoutAnnotations(after.RootElement), StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // A canonical rendering of a schema with retrieval annotations stripped. Object keys are sorted so key
    // order is not mistaken for a contract change, and SCALARS ARE COMPARED BY VALUE, NOT BY THEIR SOURCE TEXT.
    //
    // That last part is not a nicety. An earlier version used the raw text, which preserves whatever escaping
    // the document happened to arrive with — so a schema stored with "\u0027" compared unequal to the identical
    // schema written by a serialiser that emits "'", and the annotation-only exclusion silently stopped
    // applying. The effect was that any schema which had ever round-tripped through a different JSON writer
    // could never receive an annotation edit again, and the error it produced ("bump the version") pointed
    // nowhere near the cause. Found when exactly the two richest live schemas refused their annotations.
    private static string WithoutAnnotations(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var parts = element.EnumerateObject()
                    .Where(property => !string.Equals(property.Name, Retrieval.RetrievalMapping.Keyword, StringComparison.Ordinal))
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => $"{System.Text.Json.JsonSerializer.Serialize(property.Name)}:{WithoutAnnotations(property.Value)}");
                return "{" + string.Join(",", parts) + "}";
            case System.Text.Json.JsonValueKind.Array:
                return "[" + string.Join(",", element.EnumerateArray().Select(WithoutAnnotations)) + "]";
            case System.Text.Json.JsonValueKind.String:
                return System.Text.Json.JsonSerializer.Serialize(element.GetString());
            case System.Text.Json.JsonValueKind.Number:
                // Canonical numeric form, so 1 and 1.0 are the same contract. Integers keep full precision.
                return element.TryGetInt64(out var whole)
                    ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : element.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            default:
                return element.GetRawText();
        }
    }

    private static bool NotesExist(ISqliteConnectionFactory connectionFactory, string type)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM notes WHERE type = $t AND deleted = 0);";
        command.Parameters.AddWithValue("$t", type);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    // Caller holds _gate (or is the ctor before publication).
    private void Index(SchemaDefinition definition)
    {
        _generation++;
        _byTypeVersion[Key(definition.Type, definition.Version)] = definition;
        if (!_latestByType.TryGetValue(definition.Type, out var existing) || definition.Version >= existing.Version)
        {
            _latestByType[definition.Type] = definition;
        }
    }

    private static (string Type, int Version) ParseId(string json, string resourceName)
    {
        var node = JsonNode.Parse(json)
            ?? throw new InvalidOperationException($"Schema '{resourceName}' is not valid JSON.");
        var id = node["$id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Schema '{resourceName}' is missing a '$id' of the form 'type@version'.");

        var at = id.LastIndexOf('@');
        if (at <= 0 || at == id.Length - 1 || !int.TryParse(id[(at + 1)..], out var version))
        {
            throw new InvalidOperationException($"Schema '$id' must be 'type@version' but was '{id}'.");
        }

        return (id[..at], version);
    }

    private static string Key(string type, int version) => $"{type}@{version}";
}
