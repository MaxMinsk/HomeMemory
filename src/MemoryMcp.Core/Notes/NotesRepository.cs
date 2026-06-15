using System.Globalization;
using System.Text.Json.Nodes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Reads and writes notes. Every write validates the payload against the type's schema,
/// upserts by (domain, type, dedup_key), and appends a <c>note_events</c> audit record.
/// </summary>
public sealed class NotesRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SchemaRegistry _registry;
    private readonly SchemaValidator _validator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a repository over the given database, schema registry and clock.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="registry">Schema registry used for validation and version stamping.</param>
    /// <param name="timeProvider">Clock for timestamps; defaults to the system clock.</param>
    public NotesRepository(ISqliteConnectionFactory connectionFactory, SchemaRegistry registry, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _validator = new SchemaValidator(registry);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Inserts a new note or updates the existing one sharing the same (domain, type, dedup_key).
    /// The payload is validated before any write; an invalid payload throws
    /// <see cref="NoteValidationException"/> and persists nothing.
    /// </summary>
    /// <param name="domain">Namespace, e.g. <c>memory-mcp</c>.</param>
    /// <param name="type">Schema discriminator, e.g. <c>backlog_item</c>.</param>
    /// <param name="title">Human-readable title (indexed for search).</param>
    /// <param name="body">Free-text body (indexed for search).</param>
    /// <param name="payloadJson">Typed payload as JSON; validated against the type schema.</param>
    /// <param name="tagsJson">Tags as a JSON array string.</param>
    /// <param name="dedupKey">Stable key for upsert; when null, always inserts.</param>
    /// <param name="sourceAgent">Provenance: who is writing.</param>
    /// <returns>The note id and whether it was created.</returns>
    public UpsertResult Upsert(
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent)
    {
        var validation = _validator.Validate(type, payloadJson ?? "{}");
        if (!validation.IsValid)
        {
            throw new NoteValidationException(validation.Errors);
        }

        var schemaVer = _registry.GetLatest(type)!.Version;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var existing = dedupKey is null ? null : FindByDedup(connection, transaction, domain, type, dedupKey);

        string id;
        bool created;
        JsonObject? before = null;

        if (existing is null)
        {
            id = Guid.NewGuid().ToString("N");
            created = true;
            Insert(connection, transaction, id, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc);
        }
        else
        {
            id = existing.Id;
            created = false;
            before = Snapshot(existing.Title, existing.Body, existing.PayloadJson, existing.TagsJson, existing.Status, existing.SchemaVer);
            Update(connection, transaction, id, title, body, payloadJson, tagsJson, sourceAgent, schemaVer, nowUtc);
        }

        var after = Snapshot(title, body, payloadJson, tagsJson, existing?.Status ?? "active", schemaVer);
        var op = created ? "create" : "update";
        AppendEvent(connection, transaction, id, op, sourceAgent, nowUtc, BuildDiff(op, before, after));

        transaction.Commit();
        return new UpsertResult(id, created);
    }

    private static ExistingNote? FindByDedup(SqliteConnection connection, SqliteTransaction transaction, string domain, string type, string dedupKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, title, body, payload_json, tags_json, status, schema_ver " +
            "FROM notes WHERE domain = $d AND type = $t AND dedup_key = $k LIMIT 1;";
        command.Parameters.AddWithValue("$d", domain);
        command.Parameters.AddWithValue("$t", type);
        command.Parameters.AddWithValue("$k", dedupKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ExistingNote(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6));
    }

    private static void Insert(
        SqliteConnection connection, SqliteTransaction transaction, string id, string domain, string type,
        string? title, string? body, string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent,
        int schemaVer, string nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO notes (id, domain, type, title, body, payload_json, tags_json, dedup_key, status, " +
            "created_utc, updated_utc, source_agent, schema_ver, deleted) " +
            "VALUES ($id, $d, $ty, $title, $body, $payload, $tags, $dedup, 'active', $now, $now, $agent, $ver, 0);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$d", domain);
        command.Parameters.AddWithValue("$ty", type);
        AddNullable(command, "$title", title);
        AddNullable(command, "$body", body);
        AddNullable(command, "$payload", payloadJson);
        AddNullable(command, "$tags", tagsJson);
        AddNullable(command, "$dedup", dedupKey);
        AddNullable(command, "$agent", sourceAgent);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$ver", schemaVer);
        command.ExecuteNonQuery();
    }

    private static void Update(
        SqliteConnection connection, SqliteTransaction transaction, string id,
        string? title, string? body, string? payloadJson, string? tagsJson, string? sourceAgent, int schemaVer, string nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE notes SET title = $title, body = $body, payload_json = $payload, tags_json = $tags, " +
            "source_agent = $agent, schema_ver = $ver, updated_utc = $now WHERE id = $id;";
        AddNullable(command, "$title", title);
        AddNullable(command, "$body", body);
        AddNullable(command, "$payload", payloadJson);
        AddNullable(command, "$tags", tagsJson);
        AddNullable(command, "$agent", sourceAgent);
        command.Parameters.AddWithValue("$ver", schemaVer);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void AppendEvent(
        SqliteConnection connection, SqliteTransaction transaction, string noteId, string op, string? actor, string ts, string diffJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO note_events (event_id, note_id, op, actor, ts, diff_json) " +
            "VALUES ($e, $n, $op, $actor, $ts, $diff);";
        command.Parameters.AddWithValue("$e", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$n", noteId);
        command.Parameters.AddWithValue("$op", op);
        AddNullable(command, "$actor", actor);
        command.Parameters.AddWithValue("$ts", ts);
        command.Parameters.AddWithValue("$diff", diffJson);
        command.ExecuteNonQuery();
    }

    private static JsonObject Snapshot(string? title, string? body, string? payloadJson, string? tagsJson, string status, int schemaVer) =>
        new()
        {
            ["title"] = title,
            ["body"] = body,
            ["payload"] = payloadJson,
            ["tags"] = tagsJson,
            ["status"] = status,
            ["schema_ver"] = schemaVer,
        };

    private static string BuildDiff(string op, JsonObject? before, JsonObject after)
    {
        var diff = new JsonObject { ["op"] = op };
        if (before is not null)
        {
            diff["before"] = before;
        }

        diff["after"] = after;
        return diff.ToJsonString();
    }

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private sealed record ExistingNote(
        string Id, string? Title, string? Body, string? PayloadJson, string? TagsJson, string Status, int SchemaVer);
}
