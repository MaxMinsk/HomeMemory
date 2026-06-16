using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Security;

/// <summary>A stored bearer token's metadata (never the raw token, only its hash is persisted).</summary>
/// <param name="Id">Short id used to reference/revoke the token.</param>
/// <param name="Label">Optional agent/purpose label (provenance).</param>
/// <param name="Domains">Allowed domains: <c>*</c> for all, else a comma-separated normalized list.</param>
/// <param name="CreatedUtc">When the token was created (ISO-8601 UTC).</param>
/// <param name="RevokedUtc">When revoked (ISO-8601 UTC), or null if active.</param>
public sealed record TokenRecord(string Id, string? Label, string Domains, string CreatedUtc, string? RevokedUtc);

/// <summary>
/// Database-backed bearer tokens with per-token domain scope (MEMP-032), so different agents can be limited
/// to one domain, several, or <c>*</c> (all). Only the SHA-256 hash of a token is stored. The env
/// <c>MEMORY_BEARER_TOKEN</c> remains a separate root token resolved outside this store.
/// </summary>
public sealed class TokenStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store over the database and clock.</summary>
    public TokenStore(ISqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Lowercase hex SHA-256 of a raw token (what is stored/looked up).</summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    /// <summary>The allowed-domain set for a stored <c>domains</c> value: null = unrestricted (<c>*</c>/empty).</summary>
    public static IReadOnlyCollection<string>? AllowedDomains(string domains)
    {
        if (string.IsNullOrWhiteSpace(domains) || domains.Trim() == "*")
        {
            return null;
        }

        return domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Identifiers.Normalize).ToList();
    }

    /// <summary>Creates a token from a raw value + scope; stores only its hash. Returns the new record.</summary>
    public TokenRecord Add(string? label, string domains, string rawToken)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var now = NowUtc();
        var normalized = NormalizeDomains(domains);

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tokens (id, token_hash, label, domains, created_utc) VALUES ($id, $h, $l, $d, $now);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$h", Hash(rawToken));
        command.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("$d", normalized);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return new TokenRecord(id, label, normalized, now, null);
    }

    /// <summary>Resolves a presented raw token to its active record, or null if unknown/revoked.</summary>
    public TokenRecord? Resolve(string rawToken)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, label, domains, created_utc, revoked_utc FROM tokens WHERE token_hash = $h AND revoked_utc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$h", Hash(rawToken));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Lists all tokens (active and revoked), newest first.</summary>
    public IReadOnlyList<TokenRecord> List()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, label, domains, created_utc, revoked_utc FROM tokens ORDER BY created_utc DESC;";
        var rows = new List<TokenRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    /// <summary>Revokes a token by id; returns true if an active token was revoked.</summary>
    public bool Revoke(string id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tokens SET revoked_utc = $now WHERE id = $id AND revoked_utc IS NULL;";
        command.Parameters.AddWithValue("$now", NowUtc());
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    private static string NormalizeDomains(string domains)
    {
        var allowed = AllowedDomains(domains);
        return allowed is null ? "*" : string.Join(",", allowed);
    }

    private static TokenRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));

    private string NowUtc() => _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
