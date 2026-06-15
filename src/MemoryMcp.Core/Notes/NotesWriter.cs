using System.Globalization;
using System.Text.Json.Nodes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Write-side note access: upsert, journal capture, links, and lifecycle (archive/supersede).
/// Every write validates the payload against the type schema and appends a <c>note_events</c> audit record.
/// </summary>
public sealed class NotesWriter
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SchemaRegistry _registry;
    private readonly SchemaValidator _validator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a writer over the given database, schema registry and clock.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="registry">Schema registry used for validation and version stamping.</param>
    /// <param name="timeProvider">Clock for timestamps; defaults to the system clock.</param>
    public NotesWriter(ISqliteConnectionFactory connectionFactory, SchemaRegistry registry, TimeProvider? timeProvider = null)
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
        var nowUtc = NowUtc();

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
            before = NoteAudit.Snapshot(existing.Title, existing.Body, existing.PayloadJson, existing.TagsJson, existing.Status, existing.SchemaVer);
            Update(connection, transaction, id, title, body, payloadJson, tagsJson, sourceAgent, schemaVer, nowUtc);
        }

        var after = NoteAudit.Snapshot(title, body, payloadJson, tagsJson, existing?.Status ?? "active", schemaVer);
        var op = created ? "create" : "update";
        NoteAudit.Append(connection, transaction, id, op, sourceAgent, nowUtc, NoteAudit.BuildDiff(op, before, after));

        transaction.Commit();
        return new UpsertResult(id, created);
    }

    /// <summary>
    /// Appends a schema-less free-text journal note (type <c>journal</c>, no payload, no dedup) and audits it.
    /// This is the capture-first path for raw human notes; structuring happens later, agent-side.
    /// </summary>
    /// <param name="domain">Namespace, e.g. <c>kitchen</c>.</param>
    /// <param name="text">The raw free-text content (stored as the body).</param>
    /// <param name="sourceAgent">Provenance: who is writing.</param>
    /// <returns>The new note id.</returns>
    public string AppendJournal(string domain, string text, string? sourceAgent = null)
    {
        var nowUtc = NowUtc();
        var id = Guid.NewGuid().ToString("N");

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        Insert(connection, transaction, id, domain, "journal", null, text, null, null, null, sourceAgent, 0, nowUtc);
        NoteAudit.Append(connection, transaction, id, "create", sourceAgent, nowUtc,
            new JsonObject { ["op"] = "create", ["type"] = "journal" }.ToJsonString());
        transaction.Commit();
        return id;
    }

    /// <summary>Creates a directed link <paramref name="rel"/> from one note to another and audits it.</summary>
    /// <param name="fromId">Source note id.</param>
    /// <param name="toId">Target note id.</param>
    /// <param name="rel">Relationship verb, e.g. <c>depends_on</c>, <c>derived_from</c>.</param>
    public void Link(string fromId, string toId, string rel)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO note_links (from_id, to_id, rel, created_utc) VALUES ($f, $t, $r, $now);";
            command.Parameters.AddWithValue("$f", fromId);
            command.Parameters.AddWithValue("$t", toId);
            command.Parameters.AddWithValue("$r", rel);
            command.Parameters.AddWithValue("$now", nowUtc);
            command.ExecuteNonQuery();
        }

        var diff = new JsonObject { ["op"] = "link", ["to"] = toId, ["rel"] = rel }.ToJsonString();
        NoteAudit.Append(connection, transaction, fromId, "link", null, nowUtc, diff);
        transaction.Commit();
    }

    /// <summary>Soft-archives a note (status = archived). Returns false if no active note has that id.</summary>
    /// <param name="id">The note id.</param>
    public bool Archive(string id)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        int affected;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE notes SET status = 'archived', updated_utc = $now WHERE id = $id AND deleted = 0 AND status = 'active';";
            command.Parameters.AddWithValue("$now", nowUtc);
            command.Parameters.AddWithValue("$id", id);
            affected = command.ExecuteNonQuery();
        }

        if (affected == 0)
        {
            return false; // transaction disposed -> rolled back
        }

        NoteAudit.Append(connection, transaction, id, "archive", null, nowUtc, new JsonObject { ["op"] = "archive" }.ToJsonString());
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Marks <paramref name="oldId"/> as superseded by <paramref name="newId"/>, links them
    /// (<c>supersedes</c>) and audits it. Returns false if the old note is not active.
    /// </summary>
    /// <param name="oldId">The note being replaced.</param>
    /// <param name="newId">The replacement note.</param>
    public bool Supersede(string oldId, string newId)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        int affected;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE notes SET status = 'superseded', updated_utc = $now WHERE id = $id AND deleted = 0 AND status = 'active';";
            command.Parameters.AddWithValue("$now", nowUtc);
            command.Parameters.AddWithValue("$id", oldId);
            affected = command.ExecuteNonQuery();
        }

        if (affected == 0)
        {
            return false;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO note_links (from_id, to_id, rel, created_utc) VALUES ($f, $t, 'supersedes', $now);";
            command.Parameters.AddWithValue("$f", newId);
            command.Parameters.AddWithValue("$t", oldId);
            command.Parameters.AddWithValue("$now", nowUtc);
            command.ExecuteNonQuery();
        }

        NoteAudit.Append(connection, transaction, oldId, "supersede", null, nowUtc, new JsonObject { ["op"] = "supersede", ["by"] = newId }.ToJsonString());
        transaction.Commit();
        return true;
    }

    /// <summary>Restores an archived/superseded note to active. Returns false if no such note exists.</summary>
    /// <param name="id">The note id.</param>
    public bool Restore(string id)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        int affected;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE notes SET status = 'active', updated_utc = $now WHERE id = $id AND deleted = 0 AND status IN ('archived', 'superseded');";
            command.Parameters.AddWithValue("$now", nowUtc);
            command.Parameters.AddWithValue("$id", id);
            affected = command.ExecuteNonQuery();
        }

        if (affected == 0)
        {
            return false;
        }

        NoteAudit.Append(connection, transaction, id, "restore", null, nowUtc, new JsonObject { ["op"] = "restore" }.ToJsonString());
        transaction.Commit();
        return true;
    }

    /// <summary>Removes a directed link and audits it. Returns the number of link rows deleted (0 if none matched).</summary>
    /// <param name="fromId">Source note id.</param>
    /// <param name="toId">Target note id.</param>
    /// <param name="rel">Relationship verb.</param>
    public int Unlink(string fromId, string toId, string rel)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        int affected;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM note_links WHERE from_id = $f AND to_id = $t AND rel = $r;";
            command.Parameters.AddWithValue("$f", fromId);
            command.Parameters.AddWithValue("$t", toId);
            command.Parameters.AddWithValue("$r", rel);
            affected = command.ExecuteNonQuery();
        }

        if (affected == 0)
        {
            return 0;
        }

        var diff = new JsonObject { ["op"] = "unlink", ["to"] = toId, ["rel"] = rel }.ToJsonString();
        NoteAudit.Append(connection, transaction, fromId, "unlink", null, nowUtc, diff);
        transaction.Commit();
        return affected;
    }

    private string NowUtc() => _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

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

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private sealed record ExistingNote(
        string Id, string? Title, string? Body, string? PayloadJson, string? TagsJson, string Status, int SchemaVer);
}
