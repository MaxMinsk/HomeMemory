using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Maintenance;

/// <summary>Result of moving a domain's notes under a target domain as a project.</summary>
/// <param name="SourceDomain">The domain being moved (becomes the project name).</param>
/// <param name="TargetDomain">The domain the notes move into.</param>
/// <param name="ToMove">Active notes that will move (or moved).</param>
/// <param name="Collisions">"type:dedupKey" pairs that already exist in the target (block an apply).</param>
/// <param name="Applied">True when the move was written.</param>
public sealed record DomainMoveReport(
    string SourceDomain, string TargetDomain, int ToMove, IReadOnlyList<string> Collisions, bool Applied);

/// <summary>
/// Consolidation move (MEMP-148): re-homes every active note in <c>sourceDomain</c> into <c>targetDomain</c>
/// with <c>project = sourceDomain</c> — e.g. <c>memory-mcp</c> → <c>development</c>/project=memory-mcp. The
/// dedup unique index <c>(domain, type, dedup_key)</c> makes an apply atomic: a clash with an existing target
/// note rolls the whole move back, so we surface clashes in the dry run and refuse to apply while any remain.
/// </summary>
public static class DomainMove
{
    /// <summary>Dry-runs (counts + clash list) or applies the move. Apply is refused if any clash exists.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="sourceDomain">Domain to move (becomes the project).</param>
    /// <param name="targetDomain">Domain to move into.</param>
    /// <param name="apply">When false, only reports; when true, writes (unless clashes block it).</param>
    public static DomainMoveReport Run(ISqliteConnectionFactory connectionFactory, string sourceDomain, string targetDomain, bool apply)
    {
        var src = Identifiers.Normalize(sourceDomain);
        var target = Identifiers.Normalize(targetDomain);
        if (src.Length == 0 || target.Length == 0 || src == target)
        {
            throw new ArgumentException("Source and target domains must differ and be non-empty.", nameof(sourceDomain));
        }

        using var connection = connectionFactory.Create();
        var toMove = Convert.ToInt32(Scalar(connection,
            "SELECT count(*) FROM notes WHERE domain = $s AND deleted = 0;", src, target));
        var collisions = Collisions(connection, src, target);

        if (apply && collisions.Count == 0)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE notes SET domain = $t, project = $s WHERE domain = $s AND deleted = 0;";
            update.Parameters.AddWithValue("$t", target);
            update.Parameters.AddWithValue("$s", src);
            update.ExecuteNonQuery();
            return new DomainMoveReport(src, target, toMove, collisions, true);
        }

        return new DomainMoveReport(src, target, toMove, collisions, false);
    }

    private static List<string> Collisions(SqliteConnection connection, string src, string target)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.type || ':' || s.dedup_key FROM notes s WHERE s.domain = $s AND s.deleted = 0 AND s.dedup_key IS NOT NULL " +
            "AND EXISTS (SELECT 1 FROM notes t WHERE t.domain = $t AND t.type = s.type AND t.dedup_key = s.dedup_key) LIMIT 100;";
        command.Parameters.AddWithValue("$s", src);
        command.Parameters.AddWithValue("$t", target);

        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static object? Scalar(SqliteConnection connection, string sql, string src, string target)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$s", src);
        command.Parameters.AddWithValue("$t", target);
        return command.ExecuteScalar();
    }
}
