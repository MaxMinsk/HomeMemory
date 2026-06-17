using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Maintenance;

/// <summary>Result of a project backfill pass.</summary>
/// <param name="NotesUpdated">Notes whose envelope <c>project</c> was (or would be) set from <c>payload.project</c>.</param>
/// <param name="Applied">True when the change was written; false for a dry run.</param>
public sealed record ProjectBackfillReport(int NotesUpdated, bool Applied);

/// <summary>
/// Sets the envelope <c>project</c> column from <c>payload.project</c> for notes that carry the project only in
/// their payload and have no envelope value yet (MEMP-151) — e.g. notes written before the auto-derive. Re-runnable
/// and idempotent (only touches rows where project IS NULL). Migration 0012 did this once; this exposes it on demand.
/// </summary>
public static class ProjectBackfill
{
    private const string Predicate = "deleted = 0 AND project IS NULL AND json_extract(payload_json, '$.project') IS NOT NULL";

    /// <summary>Runs the backfill (or a dry run, reporting how many rows would change).</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="apply">When false, only counts; when true, writes.</param>
    public static ProjectBackfillReport Run(ISqliteConnectionFactory connectionFactory, bool apply)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        if (!apply)
        {
            command.CommandText = $"SELECT count(*) FROM notes WHERE {Predicate};";
            return new ProjectBackfillReport(Convert.ToInt32(command.ExecuteScalar()), false);
        }

        command.CommandText = $"UPDATE notes SET project = lower(json_extract(payload_json, '$.project')) WHERE {Predicate};";
        return new ProjectBackfillReport(command.ExecuteNonQuery(), true);
    }
}
