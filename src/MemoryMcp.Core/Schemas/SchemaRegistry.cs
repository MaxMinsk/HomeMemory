using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Schemas;

/// <summary>
/// Loads payload JSON Schemas (embedded as resources, named by their <c>$id</c> = <c>type@version</c>)
/// and persists them to the <c>schemas</c> table so agents can fetch the contract before writing.
/// </summary>
public sealed class SchemaRegistry
{
    private readonly Dictionary<string, SchemaDefinition> _byTypeVersion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SchemaDefinition> _latestByType = new(StringComparer.Ordinal);

    private SchemaRegistry(IEnumerable<SchemaDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            _byTypeVersion[Key(definition.Type, definition.Version)] = definition;
            if (!_latestByType.TryGetValue(definition.Type, out var existing) || definition.Version > existing.Version)
            {
                _latestByType[definition.Type] = definition;
            }
        }
    }

    /// <summary>All registered schema definitions.</summary>
    public IReadOnlyCollection<SchemaDefinition> All => _byTypeVersion.Values;

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
    public SchemaDefinition? GetLatest(string type) =>
        _latestByType.TryGetValue(type, out var definition) ? definition : null;

    /// <summary>Returns a specific <paramref name="type"/> at <paramref name="version"/>, or <c>null</c>.</summary>
    public SchemaDefinition? Get(string type, int version) =>
        _byTypeVersion.TryGetValue(Key(type, version), out var definition) ? definition : null;

    /// <summary>Idempotently upserts every registered schema into the <c>schemas</c> table.</summary>
    /// <param name="connectionFactory">Factory for the database to write to.</param>
    public void SyncToDatabase(ISqliteConnectionFactory connectionFactory)
    {
        using var connection = connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        foreach (var definition in _byTypeVersion.Values)
        {
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

    private static (string Type, int Version) ParseId(string json, string resourceName)
    {
        var node = JsonNode.Parse(json)
            ?? throw new InvalidOperationException($"Schema '{resourceName}' is not valid JSON.");
        var id = node["$id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Schema '{resourceName}' is missing a '$id'.");

        var at = id.LastIndexOf('@');
        if (at <= 0 || at == id.Length - 1 || !int.TryParse(id[(at + 1)..], out var version))
        {
            throw new InvalidOperationException($"Schema '$id' must be 'type@version' but was '{id}'.");
        }

        return (id[..at], version);
    }

    private static string Key(string type, int version) => $"{type}@{version}";
}
