using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Maintenance;

/// <summary>The result of a CAS reconciliation pass.</summary>
/// <param name="OrphanBlobs">Blob hashes no attachment references (deleted when <c>apply</c>).</param>
/// <param name="FreedBytes">Total bytes the orphan blobs occupied.</param>
/// <param name="MissingBlobs">Attachment hashes whose blob file is absent (reported, never auto-fixed).</param>
/// <param name="Applied">Whether orphans were actually deleted (false = dry run).</param>
public sealed record ReconcileReport(IReadOnlyList<string> OrphanBlobs, long FreedBytes, IReadOnlyList<string> MissingBlobs, bool Applied);

/// <summary>
/// One-off content-addressed-store reconciliation: deletes blobs that no <c>attachments</c> row references
/// (cleans historical orphans) and reports attachments whose blob is missing. Admin/ops only.
/// </summary>
public static class BlobReconciler
{
    /// <summary>Scans the store against the database. When <paramref name="apply"/> is false, reports without deleting.</summary>
    public static ReconcileReport Reconcile(ISqliteConnectionFactory connectionFactory, BlobStore blobs, bool apply)
    {
        var referenced = ReferencedShas(connectionFactory);

        var orphans = new List<string>();
        long freed = 0;
        foreach (var sha in blobs.EnumerateShas())
        {
            if (!referenced.Contains(sha))
            {
                freed += blobs.Size(sha);
                orphans.Add(sha);
                if (apply)
                {
                    blobs.Delete(sha);
                }
            }
        }

        var missing = referenced.Where(sha => !blobs.Exists(sha)).ToList();
        return new ReconcileReport(orphans, freed, missing, apply);
    }

    private static HashSet<string> ReferencedShas(ISqliteConnectionFactory connectionFactory)
    {
        using var connection = connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT sha256 FROM attachments;";

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            referenced.Add(reader.GetString(0));
        }

        return referenced;
    }
}
