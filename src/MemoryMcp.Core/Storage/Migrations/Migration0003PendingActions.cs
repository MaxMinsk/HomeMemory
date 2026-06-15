using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds the <c>pending_actions</c> table (<c>user_version</c> 3): a two-phase confirmation log for
/// destructive operations. A destructive tool records a <c>pending</c> row; a later confirm flips it
/// to <c>executed</c> via a compare-and-swap (only if still pending), guaranteeing exactly-once
/// execution. Rows are the audit trail of who requested/confirmed what and when.
/// </summary>
public sealed class Migration0003PendingActions : IMigration
{
    /// <inheritdoc />
    public int Version => 3;

    /// <inheritdoc />
    public string Name => "0003_pending_actions";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    private const string Sql = @"
CREATE TABLE pending_actions (
    token        TEXT PRIMARY KEY,
    action       TEXT NOT NULL,
    target_id    TEXT NOT NULL,
    second_id    TEXT,
    summary      TEXT,
    requested_by TEXT,
    status       TEXT NOT NULL DEFAULT 'pending',
    created_utc  TEXT NOT NULL,
    resolved_by  TEXT,
    resolved_utc TEXT
);

CREATE INDEX ix_pending_status ON pending_actions (status, created_utc);
";
}
