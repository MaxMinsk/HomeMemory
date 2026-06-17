using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Maintenance;

/// <summary>Result of a scoped purge.</summary>
/// <param name="Domain">The domain scope (null = not constrained by domain).</param>
/// <param name="SourceAgent">The source-agent scope (null = not constrained by agent).</param>
/// <param name="Notes">Notes that match the scope (and will be / were hard-deleted).</param>
/// <param name="Attachments">Attachment rows that match — their CAS blobs are reclaimed later by gc-blobs.</param>
/// <param name="Applied">True when the purge was written; false for a dry run.</param>
public sealed record ScopedPurgeReport(string? Domain, string? SourceAgent, int Notes, int Attachments, bool Applied);

/// <summary>
/// Admin scoped-purge (MEMP-038): permanently removes notes matching a domain and/or a source agent, plus their
/// satellite rows (events, usage, links, attachments, pending actions). Unlike soft delete it reclaims space, so
/// it is the relief valve for a retired domain or a misbehaving agent's junk. At least one of domain/source-agent
/// is required — it refuses to purge the whole store. Hard delete fires the FTS delete trigger, so the index stays
/// in sync; orphaned CAS blobs are reclaimed by the separate gc-blobs pass. Apply runs in a single transaction.
/// </summary>
public static class ScopedPurge
{
    /// <summary>Dry-runs (counts) or applies the purge. Refuses an unscoped purge.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="domain">Optional domain scope.</param>
    /// <param name="sourceAgent">Optional source-agent scope (exact match).</param>
    /// <param name="apply">When false, only counts; when true, deletes.</param>
    public static ScopedPurgeReport Run(ISqliteConnectionFactory connectionFactory, string? domain, string? sourceAgent, bool apply)
    {
        var scopedDomain = string.IsNullOrWhiteSpace(domain) ? null : Identifiers.Normalize(domain);
        var agent = string.IsNullOrWhiteSpace(sourceAgent) ? null : sourceAgent.Trim();
        if (scopedDomain is null && agent is null)
        {
            throw new ArgumentException("A scoped purge requires a domain and/or a source agent — refusing to purge the whole store.", nameof(domain));
        }

        var where = Where(scopedDomain, agent);

        using var connection = connectionFactory.Create();
        var notes = Count(connection, $"SELECT count(*) FROM notes WHERE {where};", scopedDomain, agent);
        var attachments = Count(connection,
            $"SELECT count(*) FROM attachments WHERE note_id IN (SELECT id FROM notes WHERE {where});", scopedDomain, agent);

        if (!apply)
        {
            return new ScopedPurgeReport(scopedDomain, agent, notes, attachments, false);
        }

        using var transaction = connection.BeginTransaction();
        var subselect = $"(SELECT id FROM notes WHERE {where})";
        Execute(connection, transaction, $"DELETE FROM note_events WHERE note_id IN {subselect};", scopedDomain, agent);
        Execute(connection, transaction, $"DELETE FROM note_usage WHERE note_id IN {subselect};", scopedDomain, agent);
        Execute(connection, transaction, $"DELETE FROM note_links WHERE from_id IN {subselect} OR to_id IN {subselect};", scopedDomain, agent);
        Execute(connection, transaction, $"DELETE FROM attachments WHERE note_id IN {subselect};", scopedDomain, agent);
        Execute(connection, transaction, $"DELETE FROM pending_actions WHERE target_id IN {subselect} OR second_id IN {subselect};", scopedDomain, agent);
        Execute(connection, transaction, $"DELETE FROM notes WHERE {where};", scopedDomain, agent); // FTS delete trigger keeps the index in sync
        transaction.Commit();

        return new ScopedPurgeReport(scopedDomain, agent, notes, attachments, true);
    }

    // The shared scope predicate over the notes table (referenced directly and inside the satellite subselects).
    private static string Where(string? domain, string? agent)
    {
        var parts = new List<string>();
        if (domain is not null)
        {
            parts.Add("domain = $d");
        }

        if (agent is not null)
        {
            parts.Add("source_agent = $a");
        }

        return string.Join(" AND ", parts);
    }

    private static void Bind(SqliteCommand command, string? domain, string? agent)
    {
        if (domain is not null)
        {
            command.Parameters.AddWithValue("$d", domain);
        }

        if (agent is not null)
        {
            command.Parameters.AddWithValue("$a", agent);
        }
    }

    private static int Count(SqliteConnection connection, string sql, string? domain, string? agent)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, domain, agent);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, string? domain, string? agent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Bind(command, domain, agent);
        command.ExecuteNonQuery();
    }
}
