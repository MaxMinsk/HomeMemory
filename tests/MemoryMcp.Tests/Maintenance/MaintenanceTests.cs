using System.Text;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Maintenance;

public class MaintenanceTests
{
    [Fact]
    public void Reconcile_deletes_orphan_blobs_and_keeps_referenced()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var factory = Factory(temp);
        var blobs = new BlobStore(dir.Path, 0);
        var artifacts = new ArtifactsService(blobs, factory);
        var keep = artifacts.Put("kitchen", Encoding.UTF8.GetBytes("keep"), "k.txt", "text/plain", null, "me");
        var orphan = artifacts.Put("kitchen", Encoding.UTF8.GetBytes("orphan"), "o.txt", "text/plain", null, "me");

        // Orphan the second blob by removing its attachment row directly (so the blob lingers).
        Exec(factory, $"DELETE FROM attachments WHERE sha256 = '{orphan.Sha256}';");

        var dry = BlobReconciler.Reconcile(factory, blobs, apply: false);
        Assert.Contains(orphan.Sha256, dry.OrphanBlobs);
        Assert.DoesNotContain(keep.Sha256, dry.OrphanBlobs);
        Assert.False(dry.Applied);
        Assert.True(blobs.Exists(orphan.Sha256)); // dry run did not delete

        var applied = BlobReconciler.Reconcile(factory, blobs, apply: true);
        Assert.Contains(orphan.Sha256, applied.OrphanBlobs);
        Assert.True(applied.FreedBytes > 0);
        Assert.False(blobs.Exists(orphan.Sha256)); // orphan deleted
        Assert.True(blobs.Exists(keep.Sha256));     // referenced kept
    }

    [Fact]
    public void Reconcile_reports_attachment_with_missing_blob()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var factory = Factory(temp);
        var blobs = new BlobStore(dir.Path, 0);
        var a = new ArtifactsService(blobs, factory).Put("kitchen", Encoding.UTF8.GetBytes("x"), "x.txt", null, null, "me");
        var fakeSha = new string('a', 64);
        Exec(factory, $"UPDATE attachments SET sha256 = '{fakeSha}' WHERE id = '{a.Id}';");

        var report = BlobReconciler.Reconcile(factory, blobs, apply: false);
        Assert.Contains(fakeSha, report.MissingBlobs);
    }

    [Fact]
    public void Backfill_lowercases_domain_type_and_normalizes_tags()
    {
        using var temp = new TempDatabase();
        var factory = Factory(temp);
        InsertRawNote(factory, "n1", "Home", "Backlog_Item", "MEMP-300", """["A","A","b"]""");

        var dry = IdentifierBackfill.Run(factory, apply: false);
        Assert.Equal(1, dry.NotesUpdated);
        Assert.False(dry.Applied);

        IdentifierBackfill.Run(factory, apply: true);
        Assert.Equal("home|backlog_item|[\"a\",\"b\"]", ReadNote(factory, "n1"));
    }

    [Fact]
    public void Backfill_skips_and_reports_dedup_collisions()
    {
        using var temp = new TempDatabase();
        var factory = Factory(temp);
        InsertRawNote(factory, "lower", "home", "backlog_item", "MEMP-300", null);
        InsertRawNote(factory, "upper", "Home", "backlog_item", "MEMP-300", null); // collides once lowercased

        var report = IdentifierBackfill.Run(factory, apply: true);
        Assert.Contains("upper", report.Collisions);
        Assert.Equal("Home|backlog_item", ReadNote(factory, "upper")[..("Home|backlog_item".Length)]); // left untouched
    }

    private static SqliteConnectionFactory Factory(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return factory;
    }

    private static void Exec(SqliteConnectionFactory factory, string sql)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertRawNote(SqliteConnectionFactory factory, string id, string domain, string type, string dedupKey, string? tagsJson)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO notes (id, domain, type, title, dedup_key, tags_json, status, created_utc, updated_utc, schema_ver, deleted) " +
            "VALUES ($id, $d, $t, 'T', $k, $g, 'active', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1, 0);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$d", domain);
        command.Parameters.AddWithValue("$t", type);
        command.Parameters.AddWithValue("$k", dedupKey);
        command.Parameters.AddWithValue("$g", (object?)tagsJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string ReadNote(SqliteConnectionFactory factory, string id)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT domain, type, tags_json FROM notes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        reader.Read();
        var tags = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        return $"{reader.GetString(0)}|{reader.GetString(1)}" + (tags.Length == 0 ? string.Empty : $"|{tags}");
    }
}
