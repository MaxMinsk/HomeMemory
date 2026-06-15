using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// A single forward schema migration. Migrations are applied in ascending
/// <see cref="Version"/> order, each inside its own transaction.
/// </summary>
public interface IMigration
{
    /// <summary>Target schema version this migration produces (1-based, contiguous).</summary>
    int Version { get; }

    /// <summary>Short human-readable name, e.g. <c>0001_init</c>.</summary>
    string Name { get; }

    /// <summary>Applies the migration using the given open connection and active transaction.</summary>
    /// <param name="connection">An open connection from the factory.</param>
    /// <param name="transaction">The transaction this migration must enlist its commands in.</param>
    void Up(SqliteConnection connection, SqliteTransaction transaction);
}
