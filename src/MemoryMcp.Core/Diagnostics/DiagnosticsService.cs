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
    // 2 (MEMP-232/234): discovery tools are scope-restricted, and notes_recall/memory_context take a `tags`
    // filter (with `query` now optional) — an agent has to know whether tag-recall is available before using it.
    // 3 (MEMP-236): an unrestricted caller's readable/writable domain lists are populated instead of empty, so
    // "empty" now unambiguously means "no domains" — a caller that special-cased the old empty-means-all must know.
    private const int ContractVersion = 3;
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

    /// <summary>
    /// Reads a fresh status snapshot (health + a breakdown of what is stored). The NOTE counts
    /// (<c>noteCount</c>, <c>notesByType</c>, <c>notesByDomain</c>, <c>notesByStatus</c>) honour
    /// <paramref name="restrictToDomains"/>, so a domain-scoped caller cannot read the shape of domains it
    /// may not search (MEMP-232). Storage and operations figures (attachments, blob bytes, database size,
    /// pending confirmations) are server-wide by nature and are reported unscoped.
    /// </summary>
    /// <param name="restrictToDomains">Auth scope; null = unrestricted, empty = nothing visible.</param>
    public StatusReport Snapshot(IReadOnlyCollection<string>? restrictToDomains = null)
    {
        using var connection = _connectionFactory.Create();

        var schemaVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        // Default-visible counts mirror the default search (active only); archived/superseded are split out in notesByStatus.
        var noteCount = CountNotes(connection, activeOnly: true, restrictToDomains);
        var attachmentCount = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM attachments;"));
        var notesByType = CountBy(connection, "type", activeOnly: true, restrictToDomains);
        var notesByDomain = CountBy(connection, "domain", activeOnly: true, restrictToDomains);
        var notesByStatus = CountBy(connection, "status", activeOnly: false, restrictToDomains);
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

        // An unrestricted caller used to get two empty arrays, which read as "no domains" but meant "all of them"
        // — the caller could not tell those apart and had no other way to learn what exists (MEMP-236). It now gets
        // the domains that actually exist; `unrestricted` still says the lists are an inventory, not a limit.
        var existing = scope.IsUnrestricted ? ExistingDomains(connection) : null;
        var readable = existing
            ?? scope.AllowedDomains.Append(ScopeGuard.CommonsDomain).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        var writable = existing
            ?? scope.AllowedDomains.OrderBy(d => d, StringComparer.Ordinal).ToArray();
        var scopeInfo = new ScopeInfo(scope.IsUnrestricted, readable, writable, CommonsReadable: true);

        return new CapabilitiesReport(ServerVersion, schemaVersion, ContractVersion, types, scopeInfo,
            SearchBackendDescription, _blobs?.QuotaBytes ?? 0, ScopeGuard.CommonsDomain, SkillsHint);
    }

    // Every domain that currently holds a note, ordered. Reported to an unrestricted caller as its readable and
    // writable set: such a caller may also write to a domain that does not exist yet, so this is an inventory of
    // what is there, not a limit on what it may reach — `ScopeInfo.Unrestricted` is what distinguishes the two.
    private static string[] ExistingDomains(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT domain FROM notes WHERE deleted = 0 ORDER BY domain;";
        var domains = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            domains.Add(reader.GetString(0));
        }

        return [.. domains];
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
    private static IReadOnlyDictionary<string, long> CountBy(
        SqliteConnection connection, string column, bool activeOnly, IReadOnlyCollection<string>? restrictToDomains)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {column}, count(*) FROM notes WHERE {NoteFilters(command, activeOnly, restrictToDomains)} " +
            $"GROUP BY {column} ORDER BY {column};";
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    private static long CountNotes(SqliteConnection connection, bool activeOnly, IReadOnlyCollection<string>? restrictToDomains)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM notes WHERE {NoteFilters(command, activeOnly, restrictToDomains)};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // Shared WHERE body for the note counts: live rows, optionally active-only, bounded by the caller's scope.
    // An empty restriction means "no domain is visible" and must count nothing rather than everything (MEMP-232).
    private static string NoteFilters(SqliteCommand command, bool activeOnly, IReadOnlyCollection<string>? restrictToDomains)
    {
        var filters = new List<string> { "deleted = 0" };
        if (activeOnly)
        {
            filters.Add("status = 'active'");
        }

        if (restrictToDomains is not null)
        {
            if (restrictToDomains.Count == 0)
            {
                filters.Add("0");
            }
            else
            {
                var names = restrictToDomains.Select((domain, index) =>
                {
                    var parameter = $"$sd{index}";
                    command.Parameters.AddWithValue(parameter, domain);
                    return parameter;
                });
                filters.Add($"domain IN ({string.Join(", ", names)})");
            }
        }

        return string.Join(" AND ", filters);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
