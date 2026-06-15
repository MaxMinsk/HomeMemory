using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// Applies pending <see cref="IMigration"/> steps to bring a database up to the latest
/// schema version, tracked via SQLite's <c>PRAGMA user_version</c>. Each step runs in its
/// own transaction, so a failure rolls back cleanly and leaves the version untouched.
/// </summary>
public sealed class Migrator
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IReadOnlyList<IMigration> _migrations;

    /// <summary>Creates a migrator for the given factory and ordered migration set.</summary>
    /// <param name="connectionFactory">Factory used to open the database.</param>
    /// <param name="migrations">The migrations; versions must be contiguous starting at 1.</param>
    public Migrator(ISqliteConnectionFactory connectionFactory, IEnumerable<IMigration> migrations)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _migrations = (migrations ?? throw new ArgumentNullException(nameof(migrations)))
            .OrderBy(m => m.Version)
            .ToList();

        ValidateVersions(_migrations);
    }

    /// <summary>The highest version known to this migrator (0 when there are no migrations).</summary>
    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <summary>
    /// Applies every migration whose version is greater than the database's current
    /// <c>user_version</c>. Idempotent: a database already at the latest version is untouched.
    /// </summary>
    /// <returns>The schema version after migrating.</returns>
    public int Migrate()
    {
        using var connection = _connectionFactory.Create();

        var current = GetUserVersion(connection);
        foreach (var migration in _migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            migration.Up(connection, transaction);
            SetUserVersion(connection, transaction, migration.Version);
            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    /// <summary>Reads the current schema version from the database.</summary>
    public int GetCurrentVersion()
    {
        using var connection = _connectionFactory.Create();
        return GetUserVersion(connection);
    }

    private static void ValidateVersions(IReadOnlyList<IMigration> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            var expected = i + 1;
            if (migrations[i].Version != expected)
            {
                throw new InvalidOperationException(
                    $"Migrations must be contiguous starting at 1; expected version {expected} " +
                    $"but found {migrations[i].Version} ('{migrations[i].Name}').");
            }
        }
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // PRAGMA user_version does not accept parameters; the value is an internally-generated
        // contiguous integer (validated in the constructor), so interpolation is safe here.
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}
