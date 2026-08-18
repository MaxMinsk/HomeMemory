using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// Brings a type's stored full-text lanes back in line with its current retrieval mapping (MEMP-262).
/// <para><b>Why this is not a command someone runs.</b> The first version was exactly that, and it repeated a
/// mistake this project had already made and fixed once: shipping a feature whose last step can only be taken
/// from a shell inside the container. On a Home Assistant add-on that is not a workflow, it is a trap — the
/// same one that made semantic recall look broken until it learned to fetch its own model. Editing a type's
/// annotations is supposed to be the whole procedure, so the server notices and re-lanes by itself. The CLI
/// survives for a box where the work should be done deliberately rather than at start-up.</para>
/// <para>Work is tracked per TYPE, because a mapping belongs to a type: change one, and exactly that type's
/// notes are stale. Everything else is left alone.</para>
/// </summary>
public sealed class LaneRebuilder(ISqliteConnectionFactory connectionFactory, IRetrievalProjector projector)
{
    private readonly ISqliteConnectionFactory _connections =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    private readonly IRetrievalProjector _projector = projector ?? throw new ArgumentNullException(nameof(projector));

    /// <summary>Types whose stored lanes were computed from a mapping that is no longer the current one.</summary>
    public IReadOnlyList<string> TypesNeedingRebuild()
    {
        using var connection = _connections.Create();
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT type, mapping_hash FROM note_lane_state;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                recorded[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var stale = new List<string>();
        using (var types = connection.CreateCommand())
        {
            types.CommandText = "SELECT DISTINCT type FROM notes WHERE deleted = 0;";
            using var reader = types.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                if (!recorded.TryGetValue(type, out var hash)
                    || !string.Equals(hash, _projector.Describe(type).MappingHash, StringComparison.Ordinal))
                {
                    stale.Add(type);
                }
            }
        }

        return stale;
    }

    /// <summary>
    /// Recomputes one type's lanes and records the mapping that produced them. Returns how many notes actually
    /// changed — a plain UPDATE per note, which the existing triggers turn into an index refresh, so search
    /// stays available throughout and no FTS rebuild is involved.
    /// </summary>
    /// <param name="type">The note type to re-lane.</param>
    /// <param name="nowUtc">Timestamp to record against the mapping.</param>
    /// <param name="cancellationToken">Cancellation token; a cancelled run leaves the type marked stale.</param>
    public int Rebuild(string type, string nowUtc, CancellationToken cancellationToken = default)
    {
        using var connection = _connections.Create();
        var rows = new List<(string Id, string? Title, string? Body, string? Tags, string? Payload)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, title, body, tags_json, payload_json FROM notes WHERE deleted = 0 AND type = $t;";
            read.Parameters.AddWithValue("$t", type);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var changed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lanes = _projector.Lanes(new NoteContent(type, row.Title, row.Body, row.Tags, row.Payload));
            using var update = connection.CreateCommand();
            // The guard matters: an unchanged row must not be rewritten, or every start would touch every note
            // and fire an index refresh for each of them.
            update.CommandText =
                "UPDATE notes SET lane_primary = $p, lane_secondary = $s WHERE id = $id " +
                "AND (lane_primary IS NOT $p OR lane_secondary IS NOT $s);";
            update.Parameters.AddWithValue("$p", (object?)lanes.Primary ?? DBNull.Value);
            update.Parameters.AddWithValue("$s", (object?)lanes.Secondary ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", row.Id);
            changed += update.ExecuteNonQuery();
        }

        // Recorded only after the work is done, so an interrupted rebuild is retried rather than assumed complete.
        using var record = connection.CreateCommand();
        record.CommandText =
            "INSERT INTO note_lane_state (type, mapping_hash, updated_utc) VALUES ($t, $h, $now) " +
            "ON CONFLICT(type) DO UPDATE SET mapping_hash = excluded.mapping_hash, updated_utc = excluded.updated_utc;";
        record.Parameters.AddWithValue("$t", type);
        record.Parameters.AddWithValue("$h", _projector.Describe(type).MappingHash);
        record.Parameters.AddWithValue("$now", nowUtc);
        record.ExecuteNonQuery();
        return changed;
    }
}
