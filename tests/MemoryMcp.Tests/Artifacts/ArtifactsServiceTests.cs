using System.Text;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Artifacts;

public class ArtifactsServiceTests
{
    [Fact]
    public void Put_then_get_round_trips_metadata_and_uri()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var service = NewService(temp, dir);

        var put = service.Put("kitchen", Encoding.UTF8.GetBytes("# Borscht"), "borscht.md", "text/markdown", "note-1", "tester");
        var got = service.Get(put.Id);

        Assert.NotNull(got);
        Assert.Equal(put.Sha256, got!.Sha256);
        Assert.Equal("blob://" + put.Sha256, got.Uri);
        Assert.Equal("kitchen", got.Domain);
        Assert.Equal("note-1", got.NoteId);
        Assert.Equal("borscht.md", got.Filename);
        Assert.Equal("text/markdown", got.ContentType);
        Assert.Equal(9, got.SizeBytes);
    }

    [Fact]
    public void List_filters_by_domain_and_note()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var service = NewService(temp, dir);
        service.Put("kitchen", Encoding.UTF8.GetBytes("a"), null, null, "note-1", "t");
        service.Put("kitchen", Encoding.UTF8.GetBytes("b"), null, null, "note-2", "t");
        service.Put("work", Encoding.UTF8.GetBytes("c"), null, null, "note-9", "t");

        Assert.Equal(2, service.List("kitchen").Count);
        Assert.Single(service.List("kitchen", "note-1"));
        Assert.Single(service.List("work"));
    }

    [Fact]
    public void Put_replaces_prior_attachment_with_same_note_and_filename()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var service = NewService(temp, dir);

        service.Put("kitchen", Encoding.UTF8.GetBytes("v1"), "r.md", "text/markdown", "note-1", "t");
        service.Put("kitchen", Encoding.UTF8.GetBytes("v2"), "r.md", "text/markdown", "note-1", "t"); // same -> replace
        Assert.Single(service.List("kitchen", "note-1"));

        service.Put("kitchen", Encoding.UTF8.GetBytes("x"), "other.md", "text/markdown", "note-1", "t"); // different name -> kept
        Assert.Equal(2, service.List("kitchen", "note-1").Count);
    }

    [Fact]
    public void Get_missing_returns_null()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        Assert.Null(NewService(temp, dir).Get("nope"));
    }

    [Fact]
    public void Delete_removes_attachment_and_gcs_orphan_blob()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var blobs = new BlobStore(dir.Path, 0);
        var service = new ArtifactsService(blobs, FactoryFor(temp));
        var a = service.Put("kitchen", Encoding.UTF8.GetBytes("solo"), "a.md", "text/markdown", "n1", "t");

        Assert.True(service.Delete(a.Id));
        Assert.Null(service.Get(a.Id));
        Assert.Empty(service.List("kitchen"));
        Assert.False(blobs.Exists(a.Sha256));        // orphan blob GC'd
        Assert.False(service.Delete(a.Id));          // already gone -> false
    }

    [Fact]
    public void Delete_keeps_blob_still_referenced_by_another_attachment()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var blobs = new BlobStore(dir.Path, 0);
        var service = new ArtifactsService(blobs, FactoryFor(temp));
        var same = Encoding.UTF8.GetBytes("shared");
        var a = service.Put("kitchen", same, "a.md", "text/markdown", "n1", "t");
        var b = service.Put("kitchen", same, "b.md", "text/markdown", "n2", "t"); // same bytes -> same blob

        service.Delete(a.Id);
        Assert.True(blobs.Exists(b.Sha256));         // still referenced by b
    }

    [Fact]
    public void Replace_then_delete_leaves_no_orphan_blob()
    {
        using var temp = new TempDatabase();
        using var dir = new TempDir();
        var blobs = new BlobStore(dir.Path, 0);
        var service = new ArtifactsService(blobs, FactoryFor(temp));

        var v1 = service.Put("kitchen", Encoding.UTF8.GetBytes("version one"), "r.md", "text/markdown", "n1", "t");
        var v2 = service.Put("kitchen", Encoding.UTF8.GetBytes("version two, different"), "r.md", "text/markdown", "n1", "t");

        Assert.False(blobs.Exists(v1.Sha256)); // the replaced blob is GC'd (no orphan)
        Assert.True(blobs.Exists(v2.Sha256));

        service.Delete(v2.Id);
        Assert.False(blobs.Exists(v2.Sha256)); // back to baseline
        Assert.Equal(0, blobs.TotalBytes());
    }

    private static SqliteConnectionFactory FactoryFor(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return factory;
    }

    private static ArtifactsService NewService(TempDatabase temp, TempDir dir)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new ArtifactsService(new BlobStore(dir.Path, 0), factory);
    }
}
