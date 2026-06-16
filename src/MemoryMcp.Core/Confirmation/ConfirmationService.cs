using System.Globalization;
using MemoryMcp.Core.Artifacts;
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
        new HashSet<string>(StringComparer.Ordinal) { "archive", "supersede", "artifact_delete" };

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly NotesRepository _notes;
    private readonly ArtifactsService? _artifacts;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service over the database, note repository, artifact service and clock.</summary>
    public ConfirmationService(ISqliteConnectionFactory connectionFactory, NotesRepository notes, ArtifactsService? artifacts = null, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _notes = notes;
        _artifacts = artifacts;
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

        var targetDomain = ResolveTargetDomain(action, targetId);
        var token = Guid.NewGuid().ToString("N");
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO pending_actions (token, action, target_id, second_id, summary, requested_by, status, created_utc, target_domain) " +
            "VALUES ($tok, $a, $t, $s, $sum, $by, 'pending', $now, $td);";
        command.Parameters.AddWithValue("$tok", token);
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$t", targetId);
        command.Parameters.AddWithValue("$s", (object?)secondId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sum", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", (object?)requestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$td", (object?)targetDomain ?? DBNull.Value);
        command.ExecuteNonQuery();

        return new PendingAction(token, action, targetId, secondId, summary, "pending", nowUtc, targetDomain);
    }

    // The domain the action touches (so a confirmation can be scoped). Throws if the target is missing.
    private string ResolveTargetDomain(string action, string targetId)
    {
        if (string.Equals(action, "artifact_delete", StringComparison.Ordinal))
        {
            return (_artifacts?.Get(targetId))?.Domain
                ?? throw new ConfirmationException($"Target artifact '{targetId}' does not exist.");
        }

        return _notes.Get(targetId)?.Domain
            ?? throw new ConfirmationException($"Target note '{targetId}' does not exist.");
    }

    /// <summary>Confirms and executes a pending action exactly once, if it is in the caller's scope.</summary>
    /// <param name="token">The confirmation token.</param>
    /// <param name="restrictToDomains">Caller's allowed domains; null = unrestricted (trusted/local).</param>
    /// <param name="resolvedBy">Who confirmed (provenance).</param>
    public ConfirmationResult Confirm(string token, IReadOnlyCollection<string>? restrictToDomains, string? resolvedBy)
    {
        PendingRow row;
        using (var connection = _connectionFactory.Create())
        {
            row = Resolve(connection, token, "executed", restrictToDomains, resolvedBy); // CAS flip; throws if not pending/out of scope
        }

        // We now exclusively own the action — run it (idempotent ops; a no-op returns false).
        var executed = row.Action switch
        {
            "archive" => _notes.Archive(row.TargetId),
            "supersede" => _notes.Supersede(row.TargetId, row.SecondId!),
            "artifact_delete" => (_artifacts ?? throw new ConfirmationException("Artifact service unavailable.")).Delete(row.TargetId),
            _ => throw new ConfirmationException($"Unknown action '{row.Action}'."),
        };

        var detail = executed
            ? $"{row.Action} applied to {row.TargetId}"
            : $"{row.Action} was already a no-op for {row.TargetId}";
        return new ConfirmationResult(token, row.Action, executed, detail);
    }

    /// <summary>Lists unresolved pending actions in scope (newest first) so confirmation tokens aren't lost.</summary>
    /// <param name="restrictToDomains">Caller's allowed domains; null = unrestricted (all pending).</param>
    public IReadOnlyList<PendingAction> ListPending(IReadOnlyCollection<string>? restrictToDomains = null)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var scope = AppendDomainScope(command, restrictToDomains);
        command.CommandText =
            "SELECT token, action, target_id, second_id, summary, status, created_utc, target_domain " +
            "FROM pending_actions WHERE status = 'pending'" + scope + " ORDER BY created_utc DESC;";

        var rows = new List<PendingAction>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PendingAction(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>Cancels a pending action so it can never execute, if it is in the caller's scope.</summary>
    public ConfirmationResult Cancel(string token, IReadOnlyCollection<string>? restrictToDomains, string? resolvedBy)
    {
        using var connection = _connectionFactory.Create();
        var row = Resolve(connection, token, "cancelled", restrictToDomains, resolvedBy);
        return new ConfirmationResult(token, row.Action, false, $"{row.Action} for {row.TargetId} cancelled");
    }

    // Builds an optional " AND target_domain IN (...)" clause; null = no restriction, empty = match nothing.
    private static string AppendDomainScope(SqliteCommand command, IReadOnlyCollection<string>? restrict)
    {
        if (restrict is null)
        {
            return string.Empty;
        }

        if (restrict.Count == 0)
        {
            return " AND 0";
        }

        var placeholders = new List<string>();
        var index = 0;
        foreach (var domain in restrict)
        {
            var parameter = $"$sd{index++}";
            placeholders.Add(parameter);
            command.Parameters.AddWithValue(parameter, domain);
        }

        return $" AND target_domain IN ({string.Join(", ", placeholders)})";
    }

    // Compare-and-swap: flip pending -> newStatus only if still pending AND in scope, then return the row we won.
    private PendingRow Resolve(SqliteConnection connection, string token, string newStatus, IReadOnlyCollection<string>? restrict, string? resolvedBy)
    {
        using var transaction = connection.BeginTransaction();

        // Scope gate first: a restricted caller may only resolve tokens whose target domain is in scope.
        // Out-of-scope and unknown tokens are reported identically, so existence isn't leaked across domains.
        if (restrict is not null && !IsInScope(connection, transaction, token, restrict))
        {
            transaction.Rollback();
            throw new ConfirmationException($"Unknown confirmation token '{token}'.");
        }

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

    // True if the token's target domain is within the caller's allowed set. A missing token or a legacy
    // null-domain row is out of scope for a restricted caller (only an unrestricted caller resolves those).
    private static bool IsInScope(SqliteConnection connection, SqliteTransaction transaction, string token, IReadOnlyCollection<string> restrict)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT target_domain FROM pending_actions WHERE token = $tok;";
        command.Parameters.AddWithValue("$tok", token);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) && restrict.Contains(reader.GetString(0));
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
