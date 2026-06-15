using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>Returns the full note by id, or <c>null</c> if it does not exist.</summary>
    /// <param name="id">The note id.</param>
    public Note? Get(string id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, domain, type, title, body, payload_json, tags_json, dedup_key, status, " +
            "created_utc, updated_utc, source_agent, schema_ver, deleted " +
            "FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Note(
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

    /// <summary>
    /// Searches notes by an optional full-text <paramref name="query"/> plus structured filters.
    /// Text queries return a highlighted snippet and BM25 score; the full body is never returned.
    /// </summary>
    /// <param name="query">Full-text query; when null/blank, results are structured-only (newest first).</param>
    /// <param name="domain">Optional domain filter.</param>
    /// <param name="type">Optional type filter.</param>
    /// <param name="tags">Optional tags; every supplied tag must be present.</param>
    /// <param name="status">Envelope status filter; defaults to <c>active</c>.</param>
    /// <param name="limit">Maximum number of results.</param>
    public IReadOnlyList<SearchResult> Search(
        string? query = null, string? domain = null, string? type = null,
        IReadOnlyCollection<string>? tags = null, string status = "active", int limit = 10)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();

        var filters = new List<string> { "n.deleted = 0", "n.status = $status" };
        command.Parameters.AddWithValue("$status", status);
        if (domain is not null)
        {
            filters.Add("n.domain = $domain");
            command.Parameters.AddWithValue("$domain", domain);
        }

        if (type is not null)
        {
            filters.Add("n.type = $type");
            command.Parameters.AddWithValue("$type", type);
        }

        if (tags is not null)
        {
            var index = 0;
            foreach (var tag in tags)
            {
                var parameter = $"$tag{index++}";
                filters.Add($"EXISTS (SELECT 1 FROM json_each(n.tags_json) WHERE json_each.value = {parameter})");
                command.Parameters.AddWithValue(parameter, tag);
            }
        }

        command.Parameters.AddWithValue("$limit", limit);
        var tokens = string.IsNullOrWhiteSpace(query) ? new List<string>() : Tokenize(query!);
        var useFts = tokens.Count > 0;
        if (!useFts)
        {
            command.CommandText =
                "SELECT n.id, n.title, n.type, n.domain, n.body, 0.0 AS score " +
                "FROM notes n WHERE " + string.Join(" AND ", filters) + " " +
                "ORDER BY n.updated_utc DESC LIMIT $limit;";
        }
        else
        {
            // Quote each token as an FTS5 phrase so arbitrary input can't break MATCH syntax.
            command.Parameters.AddWithValue("$q", string.Join(' ', tokens.Select(t => $"\"{t}\"")));
            command.CommandText =
                "SELECT n.id, n.title, n.type, n.domain, n.body, bm25(notes_fts) AS score " +
                "FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid " +
                "WHERE notes_fts MATCH $q AND " + string.Join(" AND ", filters) + " " +
                "ORDER BY score LIMIT $limit;";
        }

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            var body = reader.IsDBNull(4) ? null : reader.GetString(4);
            results.Add(new SearchResult(
                reader.GetString(0),
                title,
                useFts ? BuildSnippet(title, body, tokens) : null,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(5)));
        }

        return results;
    }

    /// <summary>Creates a directed link <paramref name="rel"/> from one note to another and audits it.</summary>
    /// <param name="fromId">Source note id.</param>
    /// <param name="toId">Target note id.</param>
    /// <param name="rel">Relationship verb, e.g. <c>depends_on</c>, <c>derived_from</c>.</param>
    public void Link(string fromId, string toId, string rel)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
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
        AppendEvent(connection, transaction, fromId, "link", null, nowUtc, diff);
        transaction.Commit();
    }

    /// <summary>Soft-archives a note (status = archived). Returns false if no active note has that id.</summary>
    /// <param name="id">The note id.</param>
    public bool Archive(string id)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
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

        AppendEvent(connection, transaction, id, "archive", null, nowUtc, new JsonObject { ["op"] = "archive" }.ToJsonString());
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
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
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

        AppendEvent(connection, transaction, oldId, "supersede", null, nowUtc, new JsonObject { ["op"] = "supersede", ["by"] = newId }.ToJsonString());
        transaction.Commit();
        return true;
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

    private static List<string> Tokenize(string raw) =>
        Regex.Matches(raw, @"\w+").Select(m => m.Value).ToList();

    /// <summary>
    /// Builds a short excerpt around the first matching token (bracketed). Contentless FTS5 can't
    /// produce snippets itself, so we derive one from the stored title/body in code.
    /// </summary>
    private static string? BuildSnippet(string? title, string? body, IReadOnlyList<string> tokens)
    {
        var text = !string.IsNullOrWhiteSpace(body) ? body : title;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const int window = 120;
        foreach (var token in tokens)
        {
            var index = text!.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = Math.Max(0, index - 40);
            var length = Math.Min(text.Length - start, window);
            var fragment = text.Substring(start, length);

            var relative = fragment.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (relative >= 0)
            {
                fragment = fragment[..relative] + "[" + fragment.Substring(relative, token.Length) + "]" +
                           fragment[(relative + token.Length)..];
            }

            var prefix = start > 0 ? "…" : string.Empty;
            var suffix = start + length < text.Length ? "…" : string.Empty;
            return prefix + fragment + suffix;
        }

        return text!.Length <= window ? text : text[..window] + "…";
    }

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private sealed record ExistingNote(
        string Id, string? Title, string? Body, string? PayloadJson, string? TagsJson, string Status, int SchemaVer);
}
