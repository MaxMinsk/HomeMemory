using MemoryMcp.Core.Query;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Recomputes every note's <c>search_stems</c> with the improved RU normalization (<c>user_version</c> 16, MEMP-192):
/// yo-&gt;ye folding and fleeting-vowel collapse (perec/perc, otec/otc), so a dictionary form matches its own inflections.
/// Each <c>UPDATE notes</c> fires the existing AFTER-UPDATE trigger, which refreshes the FTS <c>stems</c> column from
/// the new value — no FTS rebuild needed. Raw columns are untouched, so exact/ID/code search is unchanged.
/// </summary>
public sealed class Migration0016FleetingVowelStems : IMigration
{
    /// <inheritdoc />
    public int Version => 16;

    /// <inheritdoc />
    public string Name => "0016_fleeting_vowel_stems";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Id, string? Stems)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, title, body, tags_json, payload_json FROM notes;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var stems = SearchStems.For(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
                rows.Add((reader.GetString(0), stems));
            }
        }

        foreach (var (id, stems) in rows)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE notes SET search_stems = $s WHERE id = $id;";
            update.Parameters.AddWithValue("$s", (object?)stems ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }
}
