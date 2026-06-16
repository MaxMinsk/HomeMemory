using System.Reflection;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Diagnostics;

/// <summary>Produces a <see cref="StatusReport"/> describing the current database, registry and blob state.</summary>
public sealed class DiagnosticsService
{
    // Bumped when the runtime tool contract changes in a way agents should detect (memory_capabilities).
    private const int ContractVersion = 1;
    private const string SearchBackendDescription = "fts5-bm25 (lexical; no vectors)";
    private const string SkillsHint =
        "Read the 'commons' domain first (skills: memory-authoring, agent-memory-use, tag-unification) before authoring notes.";

    // The build version, from the assembly (set by Directory.Build.props). Strip any "+commit" metadata.
    private static readonly string ServerVersion =
        (typeof(DiagnosticsService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0").Split('+')[0];

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
        // Default-visible counts mirror the default search (active only); archived/superseded are split out in notesByStatus.
        var noteCount = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM notes WHERE deleted = 0 AND status = 'active';"));
        var attachmentCount = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM attachments;"));
        var notesByType = CountBy(connection, "type", activeOnly: true);
        var notesByDomain = CountBy(connection, "domain", activeOnly: true);
        var notesByStatus = CountBy(connection, "status", activeOnly: false);
        var pending = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pending_actions WHERE status = 'pending';"));

        var schemas = _registry.All
            .Select(definition => $"{definition.Type}@{definition.Version}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        return new StatusReport(schemaVersion, schemas, noteCount, notesByType, notesByDomain, notesByStatus,
            attachmentCount, _blobs?.TotalBytes() ?? 0, pending, SearchBackendDescription,
            ServerVersion, _blobs?.QuotaBytes ?? 0, DatabaseSizeBytes(connection));
    }

    /// <summary>Builds the runtime contract for the caller's scope: build/schema/contract version, the note
    /// types this build knows, the caller's readable/writable domains, and search backend + limits. Lets an
    /// agent discover what the server supports on connect instead of guessing from a stale tool list.</summary>
    /// <param name="scope">The caller's request scope (drives the readable/writable domain lists).</param>
    public CapabilitiesReport Capabilities(RequestScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var connection = _connectionFactory.Create();
        var schemaVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));

        var types = _registry.All
            .GroupBy(definition => definition.Type, StringComparer.Ordinal)
            .Select(group => new NoteTypeInfo(group.Key, group.Max(d => d.Version), _registry.IsBuiltin(group.Key)))
            .OrderBy(info => info.Type, StringComparer.Ordinal)
            .ToList();

        var readable = scope.IsUnrestricted
            ? Array.Empty<string>()
            : scope.AllowedDomains.Append(ScopeGuard.CommonsDomain).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        var writable = scope.IsUnrestricted
            ? Array.Empty<string>()
            : scope.AllowedDomains.OrderBy(d => d, StringComparer.Ordinal).ToArray();
        var scopeInfo = new ScopeInfo(scope.IsUnrestricted, readable, writable, CommonsReadable: true);

        return new CapabilitiesReport(ServerVersion, schemaVersion, ContractVersion, types, scopeInfo,
            SearchBackendDescription, _blobs?.QuotaBytes ?? 0, ScopeGuard.CommonsDomain, SkillsHint);
    }

    // On-disk database size: the main file (page_count * page_size) plus the WAL/SHM sidecars when present.
    private static long DatabaseSizeBytes(SqliteConnection connection)
    {
        var pageCount = Convert.ToInt64(Scalar(connection, "PRAGMA page_count;"));
        var pageSize = Convert.ToInt64(Scalar(connection, "PRAGMA page_size;"));
        var total = pageCount * pageSize;

        var path = connection.DataSource;
        if (!string.IsNullOrEmpty(path))
        {
            total += SidecarBytes(path + "-wal") + SidecarBytes(path + "-shm");
        }

        return total;
    }

    private static long SidecarBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    // column is a fixed identifier ("type"/"domain"/"status"), never user input — safe to interpolate.
    private static IReadOnlyDictionary<string, long> CountBy(SqliteConnection connection, string column, bool activeOnly)
    {
        var statusFilter = activeOnly ? " AND status = 'active'" : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column}, count(*) FROM notes WHERE deleted = 0{statusFilter} GROUP BY {column} ORDER BY {column};";
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
