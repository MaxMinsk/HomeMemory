using System.Globalization;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Records lightweight access signals into <c>note_usage</c> (last accessed + retrieval count), separate
/// from note content/audit so reads never mutate the note. Best-effort: callers should ignore failures —
/// usage tracking must never break a read (MEMP-116).
/// </summary>
public sealed class UsageStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store over the database and clock.</summary>
    public UsageStore(ISqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Records one access of a note (bumps retrieval_count, sets last_accessed_utc).</summary>
    public void Record(string noteId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO note_usage (note_id, last_accessed_utc, retrieval_count) VALUES ($id, $now, 1) " +
            "ON CONFLICT(note_id) DO UPDATE SET last_accessed_utc = $now, retrieval_count = retrieval_count + 1;";
        command.Parameters.AddWithValue("$id", noteId);
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
