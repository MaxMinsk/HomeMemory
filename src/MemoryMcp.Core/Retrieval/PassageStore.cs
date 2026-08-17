using System.Globalization;
using System.Text.Json;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Retrieval;

/// <summary>One stored passage vector and the identity of what produced it (MEMP-196).</summary>
/// <param name="NoteId">The note this passage belongs to.</param>
/// <param name="Name">Passage group, e.g. <c>title</c> or <c>text</c>.</param>
/// <param name="Ordinal">Position within the group.</param>
/// <param name="SourcePaths">JSON paths the passage was built from.</param>
/// <param name="Vector">The L2-normalised vector.</param>
public sealed record StoredPassage(string NoteId, string Name, int Ordinal, IReadOnlyList<string> SourcePaths, float[] Vector);

/// <summary>
/// Reads and writes the embedding index (MEMP-196).
/// <para>Vectors are stored as a raw little-endian float BLOB rather than JSON: at one row per passage and
/// several passages per note this is the largest table in the database, and the cost of parsing text on every
/// search would fall on the hot path.</para>
/// </summary>
public sealed class PassageStore(ISqliteConnectionFactory connectionFactory)
{
    private readonly ISqliteConnectionFactory _connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <summary>
    /// Replaces every passage of a note, in one transaction. Replace rather than merge: a note's text changing
    /// can add, remove or renumber passages, and a partial update would leave orphans that still match queries.
    /// </summary>
    /// <param name="noteId">The note.</param>
    /// <param name="passages">Its passages, already embedded.</param>
    /// <param name="vectors">Vectors in the same order as <paramref name="passages"/>.</param>
    /// <param name="embedder">The model that produced them (its identity is stored per row).</param>
    /// <param name="mappingHash">Retrieval mapping hash that produced the passages.</param>
    /// <param name="nowUtc">Timestamp to record.</param>
    public void Replace(
        string noteId, IReadOnlyList<RetrievalPassage> passages, IReadOnlyList<float[]> vectors,
        IEmbedder embedder, string mappingHash, string nowUtc)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(embedder);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        DeleteFor(connection, transaction, noteId);

        for (var i = 0; i < passages.Count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO note_passages (note_id, passage_name, passage_ord, model_id, dimensions, mapping_hash, " +
                "content_hash, source_paths, vector, updated_utc) " +
                "VALUES ($id, $name, $ord, $model, $dim, $map, $content, $paths, $vec, $now);";
            insert.Parameters.AddWithValue("$id", noteId);
            insert.Parameters.AddWithValue("$name", passages[i].Name);
            insert.Parameters.AddWithValue("$ord", passages[i].Ordinal);
            insert.Parameters.AddWithValue("$model", embedder.ModelId);
            insert.Parameters.AddWithValue("$dim", embedder.Dimensions);
            insert.Parameters.AddWithValue("$map", mappingHash);
            insert.Parameters.AddWithValue("$content", ContentHash.Compute("passage", null, passages[i].Text, null, null));
            insert.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(passages[i].SourcePaths));
            insert.Parameters.AddWithValue("$vec", ToBlob(vectors[i]));
            insert.Parameters.AddWithValue("$now", nowUtc);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Drops every passage of a note (used when a note is purged, or the layer is switched off).</summary>
    /// <param name="noteId">The note.</param>
    public void Delete(string noteId)
    {
        using var connection = _connectionFactory.Create();
        DeleteFor(connection, null, noteId);
    }

    /// <summary>
    /// Every passage indexed with the given model and mapping, for cosine scoring. Rows from another model or
    /// mapping are excluded rather than converted — a cosine across two vector spaces is noise, not a weak signal.
    /// </summary>
    /// <param name="modelId">Model identity to match.</param>
    /// <param name="mappingHashes">Mapping hashes that are current for some type; rows outside the set are stale.</param>
    /// <param name="noteIds">Restrict to these notes (the lexical candidate pool); null = all.</param>
    public IReadOnlyList<StoredPassage> ForScoring(
        string modelId, IReadOnlyCollection<string> mappingHashes, IReadOnlyCollection<string>? noteIds = null)
    {
        ArgumentNullException.ThrowIfNull(mappingHashes);
        if (mappingHashes.Count == 0)
        {
            return Array.Empty<StoredPassage>();
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var filters = new List<string> { "model_id = $model", In(command, "mapping_hash", "m", mappingHashes) };
        command.Parameters.AddWithValue("$model", modelId);
        if (noteIds is not null)
        {
            if (noteIds.Count == 0)
            {
                return Array.Empty<StoredPassage>();
            }

            filters.Add(In(command, "note_id", "n", noteIds));
        }

        command.CommandText =
            $"SELECT note_id, passage_name, passage_ord, source_paths, vector FROM note_passages WHERE {string.Join(" AND ", filters)};";

        var passages = new List<StoredPassage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            passages.Add(new StoredPassage(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                ParsePaths(reader.GetString(3)), FromBlob((byte[])reader[4])));
        }

        return passages;
    }

    /// <summary>
    /// How much of the corpus is indexed with the current model and mapping, and how much is stale — the
    /// figures <c>memory_capabilities</c> reports so an agent can tell whether vector recall is actually live.
    /// </summary>
    /// <param name="modelId">Current model identity.</param>
    /// <param name="mappingHashes">Mapping hashes that are current for some type.</param>
    public (long Current, long Stale, long Notes) Coverage(string modelId, IReadOnlyCollection<string> mappingHashes)
    {
        ArgumentNullException.ThrowIfNull(mappingHashes);
        if (mappingHashes.Count == 0)
        {
            return (0, 0, 0);
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var current = In(command, "mapping_hash", "m", mappingHashes);
        command.CommandText =
            "SELECT " +
            $"  (SELECT count(*) FROM note_passages WHERE model_id = $model AND {current}), " +
            $"  (SELECT count(*) FROM note_passages WHERE model_id <> $model OR NOT {current}), " +
            $"  (SELECT count(DISTINCT note_id) FROM note_passages WHERE model_id = $model AND {current});";
        command.Parameters.AddWithValue("$model", modelId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : (0, 0, 0);
    }

    /// <summary>Active notes with no passage under the current model and mapping — the index-build work list.</summary>
    /// <param name="modelId">Current model identity.</param>
    /// <param name="mappingHashes">Mapping hashes that are current for some type.</param>
    /// <param name="limit">Maximum ids to return.</param>
    public IReadOnlyList<string> NeedingIndex(string modelId, IReadOnlyCollection<string> mappingHashes, int limit)
    {
        ArgumentNullException.ThrowIfNull(mappingHashes);
        if (mappingHashes.Count == 0)
        {
            return Array.Empty<string>();
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT n.id FROM notes n WHERE n.deleted = 0 AND n.status = 'active' AND NOT EXISTS (" +
            $"  SELECT 1 FROM note_passages p WHERE p.note_id = n.id AND p.model_id = $model AND {In(command, "p.mapping_hash", "m", mappingHashes)}) " +
            "ORDER BY n.updated_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$model", modelId);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    // Builds a parameterised IN clause. Written out rather than interpolated because these values reach SQL on
    // the read path, and a mapping hash or note id is not something to trust to string concatenation.
    private static string In(SqliteCommand command, string column, string prefix, IReadOnlyCollection<string> values)
    {
        var names = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"${prefix}{index++}");
            names.Add(name);
            command.Parameters.AddWithValue(name, value);
        }

        return $"{column} IN ({string.Join(", ", names)})";
    }

    private static void DeleteFor(SqliteConnection connection, SqliteTransaction? transaction, string noteId)
    {
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM note_passages WHERE note_id = $id;";
        delete.Parameters.AddWithValue("$id", noteId);
        delete.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ParsePaths(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }
}
