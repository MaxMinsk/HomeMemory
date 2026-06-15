using System.Security.Cryptography;

namespace MemoryMcp.Core.Artifacts;

/// <summary>A content hash plus the size of the stored blob.</summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the content.</param>
/// <param name="SizeBytes">Content length in bytes.</param>
public sealed record BlobRef(string Sha256, long SizeBytes);

/// <summary>
/// Content-addressed blob storage on disk: <c>&lt;root&gt;/ab/cd/&lt;sha256&gt;</c>, sharded by the
/// first two byte-pairs of the hash. Writes are deduplicated (identical content maps to one file) and
/// atomic (temp file + move). A byte quota caps total stored size. Bytes never enter SQLite or the
/// model context — callers hold only the hash/URI.
/// </summary>
public sealed class BlobStore
{
    private readonly string _root;
    private readonly long _quotaBytes;

    /// <summary>Creates the store rooted at <paramref name="root"/> with an optional byte quota (0 = unlimited).</summary>
    public BlobStore(string root, long quotaBytes)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _quotaBytes = quotaBytes;
        Directory.CreateDirectory(_root);
    }

    /// <summary>The configured byte quota (0 = unlimited).</summary>
    public long QuotaBytes => _quotaBytes;

    /// <summary>Stores content (idempotently) and returns its hash and size; throws if the quota would be exceeded.</summary>
    public BlobRef Put(byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var path = PathFor(sha);
        if (File.Exists(path))
        {
            return new BlobRef(sha, content.LongLength); // already stored — no new bytes, no quota change
        }

        if (_quotaBytes > 0 && TotalBytes() + content.LongLength > _quotaBytes)
        {
            throw new ArtifactException(
                $"Blob store quota exceeded: {TotalBytes() + content.LongLength} would exceed the {_quotaBytes}-byte limit.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, content);
        if (File.Exists(path))
        {
            File.Delete(temp); // lost a race — the content is already there (same bytes)
        }
        else
        {
            File.Move(temp, path);
        }

        return new BlobRef(sha, content.LongLength);
    }

    /// <summary>True if a blob with this hash is stored.</summary>
    public bool Exists(string sha256) => File.Exists(PathFor(sha256));

    /// <summary>Reads a blob's bytes, or <c>null</c> if it is not stored. For serving to a browser/UI
    /// (the bytes go to the human, never back through the model context).</summary>
    public byte[]? Read(string sha256)
    {
        var path = PathFor(sha256);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Deletes a blob's bytes if present (caller ensures nothing else references it). Returns true if a file was removed.</summary>
    public bool Delete(string sha256)
    {
        var path = PathFor(sha256);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>The on-disk path for a hash (whether or not it exists).</summary>
    public string PathFor(string sha256) => Path.Combine(_root, sha256[..2], sha256[2..4], sha256);

    /// <summary>Byte size of a stored blob, or 0 if absent.</summary>
    public long Size(string sha256)
    {
        var path = PathFor(sha256);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>All stored blob hashes (for reconciliation); excludes in-flight temp files. Materialized.</summary>
    public IReadOnlyList<string> EnumerateShas() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetFileName(file))
            .Where(name => name.Length == 64)
            .ToList();

    /// <summary>Total bytes currently stored (sum of blob file sizes; excludes in-flight temp files).</summary>
    public long TotalBytes() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file).Length == 64)
            .Sum(file => new FileInfo(file).Length);
}
