using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

/// <summary>Append-only audit: writes <c>note_events</c> rows and builds before/after diff snapshots.</summary>
internal static class NoteAudit
{
    /// <summary>Appends an event row within the active transaction.</summary>
    public static void Append(
        SqliteConnection connection, SqliteTransaction transaction,
        string noteId, string op, string? actor, string ts, string diffJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO note_events (event_id, note_id, op, actor, ts, diff_json) " +
            "VALUES ($e, $n, $op, $actor, $ts, $diff);";
        command.Parameters.AddWithValue("$e", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$n", noteId);
        command.Parameters.AddWithValue("$op", op);
        command.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("$ts", ts);
        command.Parameters.AddWithValue("$diff", diffJson);
        command.ExecuteNonQuery();
    }

    /// <summary>Captures the mutable note fields as a snapshot for diffing.</summary>
    public static JsonObject Snapshot(string? title, string? body, string? payloadJson, string? tagsJson, string status, int schemaVer) =>
        new()
        {
            ["title"] = title,
            ["body"] = body,
            ["payload"] = payloadJson,
            ["tags"] = tagsJson,
            ["status"] = status,
            ["schema_ver"] = schemaVer,
        };

    /// <summary>Builds the <c>diff_json</c> for an event (<c>after</c>, plus <c>before</c> on updates).</summary>
    public static string BuildDiff(string op, JsonObject? before, JsonObject after)
    {
        var diff = new JsonObject { ["op"] = op };
        if (before is not null)
        {
            diff["before"] = before;
        }

        diff["after"] = after;
        return diff.ToJsonString();
    }
}
