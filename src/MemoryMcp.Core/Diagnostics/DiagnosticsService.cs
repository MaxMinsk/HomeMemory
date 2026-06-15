using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Diagnostics;

/// <summary>Produces a <see cref="StatusReport"/> describing the current database, registry and blob state.</summary>
public sealed class DiagnosticsService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SchemaRegistry _registry;
    private readonly BlobStore? _blobs;

    /// <summary>Creates the service over the database, schema registry and (optionally) the blob store.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="registry">The schema registry to report from.</param>
    /// <param name="blobs">Blob store, for the stored-bytes figure; null reports 0.</param>
    public DiagnosticsService(ISqliteConnectionFactory connectionFactory, SchemaRegistry registry, BlobStore? blobs = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _blobs = blobs;
    }

    /// <summary>Reads a fresh status snapshot (health + a breakdown of what is stored).</summary>
    public StatusReport Snapshot()
    {
        using var connection = _connectionFactory.Create();

        var schemaVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        var noteCount = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM notes WHERE deleted = 0;"));
        var attachmentCount = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM attachments;"));
        var notesByType = CountBy(connection, "type");
        var notesByDomain = CountBy(connection, "domain");

        var schemas = _registry.All
            .Select(definition => $"{definition.Type}@{definition.Version}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        return new StatusReport(schemaVersion, schemas, noteCount, notesByType, notesByDomain, attachmentCount,
            _blobs?.TotalBytes() ?? 0, "fts5-bm25 (lexical; no vectors)");
    }

    // column is a fixed identifier ("type"/"domain"), never user input — safe to interpolate.
    private static IReadOnlyDictionary<string, long> CountBy(SqliteConnection connection, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column}, count(*) FROM notes WHERE deleted = 0 GROUP BY {column} ORDER BY {column};";
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
