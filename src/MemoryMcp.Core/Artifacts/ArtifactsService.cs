using System.Globalization;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Artifacts;

/// <summary>
/// Stores artifacts: writes bytes to the content-addressed <see cref="BlobStore"/> and records an
/// <c>attachments</c> row (identity, size, provenance, optional note link). Reads return metadata and
/// an opaque <c>blob://</c> URI — never the bytes (those are fetched out of band).
/// </summary>
public sealed class ArtifactsService
{
    private const string UriScheme = "blob://";
    private const string Columns = "id, domain, sha256, note_id, filename, content_type, size_bytes, created_utc";

    private readonly BlobStore _blobs;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service over the blob store, database and clock.</summary>
    public ArtifactsService(BlobStore blobs, ISqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _blobs = blobs;
        _connectionFactory = connectionFactory;
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Stores content as a deduplicated blob and records an attachment; returns its metadata.</summary>
    public Artifact Put(string domain, byte[] content, string? filename, string? contentType, string? noteId, string? sourceAgent)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArtifactException("A domain is required.");
        }

        var blob = _blobs.Put(content);
        var id = Guid.NewGuid().ToString("N");
        var nowUtc = NowUtc();

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        // Idempotent per (domain, note_id, filename): re-attaching the same named file to the same note
        // replaces the prior row instead of piling up duplicates (MEMP-059). Unnamed/unattached puts append.
        if (noteId is not null && !string.IsNullOrEmpty(filename))
        {
            using var supersede = connection.CreateCommand();
            supersede.Transaction = transaction;
            supersede.CommandText = "DELETE FROM attachments WHERE domain = $d AND note_id = $n AND filename = $f;";
            supersede.Parameters.AddWithValue("$d", domain);
            supersede.Parameters.AddWithValue("$n", noteId);
            supersede.Parameters.AddWithValue("$f", filename);
            supersede.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"INSERT INTO attachments ({Columns}, source_agent) " +
                "VALUES ($id, $d, $sha, $n, $f, $ct, $sz, $now, $by);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$d", domain);
            command.Parameters.AddWithValue("$sha", blob.Sha256);
            command.Parameters.AddWithValue("$n", (object?)noteId ?? DBNull.Value);
            command.Parameters.AddWithValue("$f", (object?)filename ?? DBNull.Value);
            command.Parameters.AddWithValue("$ct", (object?)contentType ?? DBNull.Value);
            command.Parameters.AddWithValue("$sz", blob.SizeBytes);
            command.Parameters.AddWithValue("$now", nowUtc);
            command.Parameters.AddWithValue("$by", (object?)sourceAgent ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new Artifact(id, domain, blob.Sha256, UriScheme + blob.Sha256, noteId, filename, contentType, blob.SizeBytes, nowUtc);
    }

    /// <summary>Returns an artifact's metadata by id, or <c>null</c>.</summary>
    public Artifact? Get(string id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM attachments WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Deletes an attachment by id and GCs its blob if no other attachment references it. Returns true if a row was removed.</summary>
    public bool Delete(string id)
    {
        using var connection = _connectionFactory.Create();

        string sha;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT sha256 FROM attachments WHERE id = $id LIMIT 1;";
            select.Parameters.AddWithValue("$id", id);
            if (select.ExecuteScalar() is not string found)
            {
                return false;
            }

            sha = found;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM attachments WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        using (var refs = connection.CreateCommand())
        {
            refs.CommandText = "SELECT EXISTS (SELECT 1 FROM attachments WHERE sha256 = $sha);";
            refs.Parameters.AddWithValue("$sha", sha);
            if (Convert.ToInt64(refs.ExecuteScalar()) == 0)
            {
                _blobs.Delete(sha); // orphaned blob — GC it
            }
        }

        return true;
    }

    /// <summary>Lists artifacts in a domain (newest first), optionally only those attached to <paramref name="noteId"/>.</summary>
    public IReadOnlyList<Artifact> List(string domain, string? noteId = null, int limit = 100)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM attachments WHERE domain = $d " +
            (noteId is null ? string.Empty : "AND note_id = $n ") +
            "ORDER BY created_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$d", domain);
        if (noteId is not null)
        {
            command.Parameters.AddWithValue("$n", noteId);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var results = new List<Artifact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private static Artifact Map(SqliteDataReader reader)
    {
        var sha = reader.GetString(2);
        return new Artifact(
            reader.GetString(0),
            reader.GetString(1),
            sha,
            UriScheme + sha,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7));
    }

    private string NowUtc() => _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
