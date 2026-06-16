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
        var definitions = new List<SchemaDefinition>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Schemas.", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.Ordinal))
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
    /// <returns>The stored definition.</returns>
    public SchemaDefinition Upsert(ISqliteConnectionFactory connectionFactory, string json)
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

        var existing = Get(type, version);
        if (existing is not null && !string.Equals(existing.Json, json, StringComparison.Ordinal) && NotesExist(connectionFactory, type))
        {
            throw new SchemaAuthoringException(
                $"Schema '{type}@{version}' is already in use by existing notes; bump the version for changes.");
        }

        var definition = new SchemaDefinition(type, version, json, compiled);
        Persist(connectionFactory, definition);
        lock (_gate)
        {
            Index(definition);
        }

        return definition;
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
                "INSERT INTO schemas (type, version, json_schema) VALUES ($t, $v, $j) " +
                "ON CONFLICT(type, version) DO UPDATE SET json_schema = excluded.json_schema;";
            command.Parameters.AddWithValue("$t", definition.Type);
            command.Parameters.AddWithValue("$v", definition.Version);
            command.Parameters.AddWithValue("$j", definition.Json);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Persist(ISqliteConnectionFactory connectionFactory, SchemaDefinition definition)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO schemas (type, version, json_schema) VALUES ($t, $v, $j) " +
            "ON CONFLICT(type, version) DO UPDATE SET json_schema = excluded.json_schema;";
        command.Parameters.AddWithValue("$t", definition.Type);
        command.Parameters.AddWithValue("$v", definition.Version);
        command.Parameters.AddWithValue("$j", definition.Json);
        command.ExecuteNonQuery();
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
