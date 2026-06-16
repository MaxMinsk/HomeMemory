using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage.Migrations;

/// <summary>
/// Adds <c>target_domain</c> to <c>pending_actions</c> (<c>user_version</c> 6) so a confirmation token is
/// bound to the domain it acts on. This lets <c>pending_actions_list</c>/<c>confirm</c>/<c>cancel</c> be
/// scoped to the caller's allowed domains (MEMP-098). Existing rows keep <c>NULL</c> (treated as global,
/// i.e. only an unrestricted caller resolves them).
/// </summary>
public sealed class Migration0006PendingActionDomain : IMigration
{
    /// <inheritdoc />
    public int Version => 6;

    /// <inheritdoc />
    public string Name => "0006_pending_action_domain";

    /// <inheritdoc />
    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE pending_actions ADD COLUMN target_domain TEXT;";
        command.ExecuteNonQuery();
    }
}
