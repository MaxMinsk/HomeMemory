using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

/// <summary>Maps a full-note row to a <see cref="Note"/>. Single source of the column list + mapping.</summary>
internal static class NoteRowMapper
{
    /// <summary>The column list every full-note query must SELECT, in this exact order.</summary>
    public const string Columns =
        "id, domain, type, title, body, payload_json, tags_json, dedup_key, status, " +
        "created_utc, updated_utc, source_agent, schema_ver, deleted";

    /// <summary>Maps the current row (selected with <see cref="Columns"/>) to a <see cref="Note"/>.</summary>
    public static Note Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt32(12),
        reader.GetInt64(13) != 0);
}
