using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// Creates and configures SQLite connections for the memory store.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>Opens a new configured connection. The caller owns its lifetime.</summary>
    SqliteConnection Create();
}

/// <summary>
/// Default <see cref="ISqliteConnectionFactory"/>. Every connection is opened with WAL
/// journaling, a busy timeout and foreign-key enforcement, so all callers share the same
/// durable, concurrency-friendly settings (a gap in the predecessor project, where these
/// were never set in code).
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a factory for the database file at <paramref name="databasePath"/>,
    /// creating its parent directory if it does not yet exist.
    /// </summary>
    /// <param name="databasePath">Filesystem path to the SQLite database file.</param>
    public SqliteConnectionFactory(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path must be provided.", nameof(databasePath));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <inheritdoc />
    public SqliteConnection Create()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // journal_mode=WAL persists in the database header; busy_timeout and foreign_keys
        // are per-connection settings re-applied on every open (including pooled reuse).
        command.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
    }
}
