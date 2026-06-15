using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Diagnostics;

/// <summary>Produces a <see cref="StatusReport"/> describing the current database and registry state.</summary>
public sealed class DiagnosticsService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SchemaRegistry _registry;

    /// <summary>Creates the service over the given database and schema registry.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="registry">The schema registry to report from.</param>
    public DiagnosticsService(ISqliteConnectionFactory connectionFactory, SchemaRegistry registry)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Reads a fresh status snapshot from the database.</summary>
    public StatusReport Snapshot()
    {
        using var connection = _connectionFactory.Create();

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(versionCommand.ExecuteScalar());

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT count(*) FROM notes WHERE deleted = 0;";
        var noteCount = Convert.ToInt64(countCommand.ExecuteScalar());

        var schemas = _registry.All
            .Select(definition => $"{definition.Type}@{definition.Version}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        return new StatusReport(schemaVersion, schemas, noteCount, "fts5-bm25 (lexical; no vectors)");
    }
}
