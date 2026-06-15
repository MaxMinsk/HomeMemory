using System.Text;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Artifacts;

public class BlobStoreTests
{
    [Fact]
    public void Put_is_content_addressed_and_deduplicated()
    {
        using var dir = new TempDir();
        var store = new BlobStore(dir.Path, quotaBytes: 0);
        var bytes = Encoding.UTF8.GetBytes("hello blob");

        var first = store.Put(bytes);
        var second = store.Put(Encoding.UTF8.GetBytes("hello blob")); // identical content

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(bytes.LongLength, first.SizeBytes);
        Assert.True(store.Exists(first.Sha256));
        Assert.Equal(bytes.LongLength, store.TotalBytes()); // stored once, not twice
    }

    [Fact]
    public void Put_shards_path_by_hash_prefix()
    {
        using var dir = new TempDir();
        var store = new BlobStore(dir.Path, 0);
        var sha = store.Put(Encoding.UTF8.GetBytes("x")).Sha256;

        var path = store.PathFor(sha);
        Assert.EndsWith(Path.Combine(sha[..2], sha[2..4], sha), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Put_enforces_quota()
    {
        using var dir = new TempDir();
        var store = new BlobStore(dir.Path, quotaBytes: 4);

        store.Put(new byte[] { 1, 2, 3 });                       // 3 bytes — fits
        Assert.Throws<ArtifactException>(() => store.Put(new byte[] { 4, 5 })); // +2 -> 5 > 4
    }

    [Fact]
    public void Put_existing_blob_does_not_recount_against_quota()
    {
        using var dir = new TempDir();
        var store = new BlobStore(dir.Path, quotaBytes: 3);
        var bytes = new byte[] { 1, 2, 3 };

        store.Put(bytes);
        var again = store.Put(bytes); // same content -> no new bytes, must not throw
        Assert.Equal(3, again.SizeBytes);
    }
}
