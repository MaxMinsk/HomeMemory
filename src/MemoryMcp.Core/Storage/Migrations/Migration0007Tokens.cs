using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds the <c>tokens</c> table (<c>user_version</c> 7): bearer tokens scoped to domains, so different
/// agents can get different access (one domain, several, or <c>*</c> for all). Only the SHA-256 hash of a
/// token is stored, never the raw value. The env <c>MEMORY_BEARER_TOKEN</c> remains the root token (MEMP-032).
/// </summary>
public sealed class Migration0007Tokens : IMigration
{
    /// <inheritdoc />
    public int Version => 7;

    /// <inheritdoc />
    public string Name => "0007_tokens";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
CREATE TABLE tokens (
    id           TEXT PRIMARY KEY,
    token_hash   TEXT NOT NULL UNIQUE,
    label        TEXT,
    domains      TEXT NOT NULL,
    created_utc  TEXT NOT NULL,
    revoked_utc  TEXT
);
";
}
