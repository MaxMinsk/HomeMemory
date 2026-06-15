using System.Globalization;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Confirmation;

/// <summary>
/// Two-phase confirmation for destructive operations. <see cref="Request"/> records a pending action
/// and returns a token; <see cref="Confirm"/> executes it exactly once via a compare-and-swap on the
/// row status (pending → executed), so concurrent or repeated confirms can't double-apply. The
/// <c>pending_actions</c> rows are the audit trail; the underlying op's own <c>note_events</c> record
/// the effect.
/// </summary>
public sealed class ConfirmationService
{
    /// <summary>Destructive actions that can be confirmed.</summary>
    public static readonly IReadOnlySet<string> SupportedActions =
        new HashSet<string>(StringComparer.Ordinal) { "archive", "supersede" };

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly NotesRepository _notes;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service over the database, note repository and clock.</summary>
    public ConfirmationService(ISqliteConnectionFactory connectionFactory, NotesRepository notes, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _notes = notes;
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Records a destructive action as pending and returns its confirmation token.</summary>
    public PendingAction Request(string action, string targetId, string? secondId, string? summary, string? requestedBy)
    {
        if (!SupportedActions.Contains(action))
        {
            throw new ConfirmationException($"Unknown destructive action '{action}'. Supported: {string.Join(", ", SupportedActions)}.");
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ConfirmationException("A target note id is required.");
        }

        if (string.Equals(action, "supersede", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(secondId))
        {
            throw new ConfirmationException("supersede requires the replacement note id (secondId).");
        }

        if (_notes.Get(targetId) is null)
        {
            throw new ConfirmationException($"Target note '{targetId}' does not exist.");
        }

        var token = Guid.NewGuid().ToString("N");
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO pending_actions (token, action, target_id, second_id, summary, requested_by, status, created_utc) " +
            "VALUES ($tok, $a, $t, $s, $sum, $by, 'pending', $now);";
        command.Parameters.AddWithValue("$tok", token);
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$t", targetId);
        command.Parameters.AddWithValue("$s", (object?)secondId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sum", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", (object?)requestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.ExecuteNonQuery();

        return new PendingAction(token, action, targetId, secondId, summary, "pending", nowUtc);
    }

    /// <summary>Confirms and executes a pending action exactly once.</summary>
    public ConfirmationResult Confirm(string token, string? resolvedBy)
    {
        PendingRow row;
        using (var connection = _connectionFactory.Create())
        {
            row = Resolve(connection, token, "executed", resolvedBy); // CAS flip; throws if not pending
        }

        // We now exclusively own the action — run it (idempotent ops; a no-op returns false).
        var executed = row.Action switch
        {
            "archive" => _notes.Archive(row.TargetId),
            "supersede" => _notes.Supersede(row.TargetId, row.SecondId!),
            _ => throw new ConfirmationException($"Unknown action '{row.Action}'."),
        };

        var detail = executed
            ? $"{row.Action} applied to {row.TargetId}"
            : $"{row.Action} was already a no-op for {row.TargetId}";
        return new ConfirmationResult(token, row.Action, executed, detail);
    }

    /// <summary>Cancels a pending action so it can never execute.</summary>
    public ConfirmationResult Cancel(string token, string? resolvedBy)
    {
        using var connection = _connectionFactory.Create();
        var row = Resolve(connection, token, "cancelled", resolvedBy);
        return new ConfirmationResult(token, row.Action, false, $"{row.Action} for {row.TargetId} cancelled");
    }

    // Compare-and-swap: flip pending -> newStatus only if still pending, then return the row we won.
    private PendingRow Resolve(SqliteConnection connection, string token, string newStatus, string? resolvedBy)
    {
        using var transaction = connection.BeginTransaction();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE pending_actions SET status = $st, resolved_by = $by, resolved_utc = $now " +
                "WHERE token = $tok AND status = 'pending';";
            update.Parameters.AddWithValue("$st", newStatus);
            update.Parameters.AddWithValue("$by", (object?)resolvedBy ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", NowUtc());
            update.Parameters.AddWithValue("$tok", token);
            if (update.ExecuteNonQuery() == 0)
            {
                var reason = DescribeUnavailable(connection, transaction, token);
                transaction.Rollback();
                throw new ConfirmationException(reason);
            }
        }

        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT action, target_id, second_id FROM pending_actions WHERE token = $tok;";
        read.Parameters.AddWithValue("$tok", token);
        using (var reader = read.ExecuteReader())
        {
            reader.Read();
            var row = new PendingRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            reader.Close();
            transaction.Commit();
            return row;
        }
    }

    private static string DescribeUnavailable(SqliteConnection connection, SqliteTransaction transaction, string token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status FROM pending_actions WHERE token = $tok;";
        command.Parameters.AddWithValue("$tok", token);
        return command.ExecuteScalar() is string status
            ? $"Confirmation '{token}' is already {status}."
            : $"Unknown confirmation token '{token}'.";
    }

    private string NowUtc() => _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private readonly record struct PendingRow(string Action, string TargetId, string? SecondId);
}
