using System.Globalization;
using System.Text.Json.Nodes;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Maintenance;

/// <summary>Result of a duplicate-content merge.</summary>
/// <param name="Domain">The domain scope (null = all domains).</param>
/// <param name="Groups">Number of duplicate-content groups found (each is 2+ identical active notes).</param>
/// <param name="Superseded">Notes that will be / were superseded into their group's canonical.</param>
/// <param name="Applied">True when the merge was written; false for a dry run.</param>
public sealed record DuplicateMergeReport(string? Domain, int Groups, int Superseded, bool Applied);

/// <summary>
/// Admin dedup/merge hygiene (MEMP-027): collapses groups of active notes that share a <c>content_hash</c> within
/// a domain (exact-content duplicates) down to one canonical note — the most-recently-updated. Each other copy is
/// superseded (status=superseded + a <c>supersedes</c> link from the canonical) and its incoming links are
/// re-pointed to the canonical, so references survive and the duplicate drops out of active search/recall. Runs in
/// one transaction; refuses nothing — a dry run reports what would change.
/// </summary>
public static class DuplicateMerge
{
    /// <summary>Dry-runs (counts) or applies the merge. Optionally scoped to one domain.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="domain">Optional domain scope (null = all domains).</param>
    /// <param name="apply">When false, only counts; when true, supersedes duplicates.</param>
    /// <param name="timeProvider">Clock for the supersede timestamp; defaults to the system clock.</param>
    public static DuplicateMergeReport Run(ISqliteConnectionFactory connectionFactory, string? domain, bool apply, TimeProvider? timeProvider = null)
    {
        var scopedDomain = string.IsNullOrWhiteSpace(domain) ? null : Identifiers.Normalize(domain);

        using var connection = connectionFactory.Create();
        var groups = FindGroups(connection, scopedDomain);
        var dupCount = groups.Sum(group => group.Count - 1);

        if (!apply || dupCount == 0)
        {
            return new DuplicateMergeReport(scopedDomain, groups.Count, dupCount, false);
        }

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        using var transaction = connection.BeginTransaction();
        var superseded = 0;
        foreach (var group in groups)
        {
            // Canonical = newest (the fetch ordered each group updated_utc DESC, id DESC), the rest are duplicates.
            var canonical = group[0];
            for (var i = 1; i < group.Count; i++)
            {
                MergeOne(connection, transaction, group[i], canonical, nowUtc);
                superseded++;
            }
        }

        transaction.Commit();
        return new DuplicateMergeReport(scopedDomain, groups.Count, superseded, true);
    }

    // Groups of 2+ active, non-deleted notes that share (domain, content_hash). Each group is ordered so the
    // newest note is first (the canonical).
    private static List<List<string>> FindGroups(SqliteConnection connection, string? domain)
    {
        using var command = connection.CreateCommand();
        var filter = domain is null ? string.Empty : "AND domain = $d ";
        command.CommandText =
            "SELECT domain, content_hash, id FROM notes " +
            $"WHERE deleted = 0 AND status = 'active' AND content_hash IS NOT NULL {filter}" +
            "ORDER BY domain, content_hash, updated_utc DESC, id DESC;";
        if (domain is not null)
        {
            command.Parameters.AddWithValue("$d", domain);
        }

        var groups = new List<List<string>>();
        string? lastKey = null;
        List<string>? current = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0) + "" + reader.GetString(1);
            if (key != lastKey)
            {
                current = new List<string>();
                groups.Add(current);
                lastKey = key;
            }

            current!.Add(reader.GetString(2));
        }

        groups.RemoveAll(group => group.Count < 2); // only actual duplicates
        return groups;
    }

    // Supersedes one duplicate into the canonical: re-point its incoming links, mark it superseded, link + audit.
    private static void MergeOne(SqliteConnection connection, SqliteTransaction transaction, string duplicate, string canonical, string nowUtc)
    {
        // Move incoming links to the canonical (OR IGNORE skips ones the canonical already has), then drop any
        // leftovers still pointing at the duplicate (collisions + a canonical->duplicate self reference).
        Execute(connection, transaction,
            "UPDATE OR IGNORE note_links SET to_id = $c WHERE to_id = $dup AND from_id <> $c;", canonical, duplicate, nowUtc);
        Execute(connection, transaction, "DELETE FROM note_links WHERE to_id = $dup;", canonical, duplicate, nowUtc);

        Execute(connection, transaction,
            "UPDATE notes SET status = 'superseded', updated_utc = $now WHERE id = $dup AND deleted = 0 AND status = 'active';",
            canonical, duplicate, nowUtc);
        Execute(connection, transaction,
            "INSERT OR IGNORE INTO note_links (from_id, to_id, rel, created_utc) VALUES ($c, $dup, 'supersedes', $now);",
            canonical, duplicate, nowUtc);

        NoteAudit.Append(connection, transaction, duplicate, "supersede", "merge", nowUtc,
            new JsonObject { ["op"] = "supersede", ["by"] = canonical, ["reason"] = "duplicate_content_merge" }.ToJsonString());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, string canonical, string duplicate, string nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$c", canonical);
        command.Parameters.AddWithValue("$dup", duplicate);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.ExecuteNonQuery();
    }
}
