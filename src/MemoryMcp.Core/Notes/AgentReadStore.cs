using System.Globalization;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Records a per-agent running count of recall/search reads into <c>agent_reads</c> (MEMP-207). Only callers
/// that identify themselves (a non-blank sourceAgent) are counted, so volume stays low and the common anonymous
/// read path never writes. Best-effort — like <see cref="UsageStore"/>, a failure must never break a read.
/// </summary>
public sealed class AgentReadStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store over the database and clock.</summary>
    public AgentReadStore(ISqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Records one recall/search by <paramref name="agent"/> (no-op for a null/blank agent).</summary>
    public void Record(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return;
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_reads (agent, read_count, last_read_utc) VALUES ($a, 1, $now) " +
            "ON CONFLICT(agent) DO UPDATE SET read_count = read_count + 1, last_read_utc = $now;";
        command.Parameters.AddWithValue("$a", agent);
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
