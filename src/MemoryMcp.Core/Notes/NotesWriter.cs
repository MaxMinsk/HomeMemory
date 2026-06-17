using System.Globalization;
using System.Text.Json.Nodes;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Query;
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
    /// <param name="project">Optional project sub-axis within the domain; null leaves it unchanged on update.</param>
    /// <returns>The note id and whether it was created.</returns>
    public UpsertResult Upsert(
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent, string? project = null)
    {
        domain = Identifiers.Normalize(domain);
        type = Identifiers.Normalize(type);
        tagsJson = Identifiers.NormalizeTags(tagsJson);
        // Fall back to payload.project when no explicit envelope project is given, so a note that only carries
        // its project in the typed payload (e.g. backlog_item) still gets the envelope axis set.
        project = Identifiers.NormalizeOptional(project ?? PayloadProject(payloadJson));

        var validation = _validator.Validate(type, payloadJson ?? "{}");
        if (!validation.IsValid)
        {
            throw new NoteValidationException(validation.Errors);
        }

        var schemaVer = _registry.GetLatest(type)!.Version;
        var nowUtc = NowUtc();

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        var (id, created) = PersistNote(connection, transaction, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc, project);
        transaction.Commit();
        return new UpsertResult(id, created, nowUtc, type, dedupKey);
    }

    // Inserts a new note or dedup-updates the existing one, plus its audit event, within an existing
    // transaction (the caller commits). Shared by Upsert and Assemble.
    private static (string Id, bool Created) PersistNote(
        SqliteConnection connection, SqliteTransaction transaction,
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent, int schemaVer, string nowUtc, string? project = null)
    {
        var existing = dedupKey is null ? null : FindByDedup(connection, transaction, domain, type, dedupKey);

        var contentHash = ContentHash.Compute(type, title, body, payloadJson, tagsJson);
        var searchStems = SearchStems.For(title, body, tagsJson, payloadJson);

        string id;
        bool created;
        JsonObject? before = null;
        if (existing is null)
        {
            id = Guid.NewGuid().ToString("N");
            created = true;
            Insert(connection, transaction, id, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc, contentHash, searchStems, project);
        }
        else
        {
            id = existing.Id;
            created = false;
            before = NoteAudit.Snapshot(existing.Title, existing.Body, existing.PayloadJson, existing.TagsJson, existing.Status, existing.SchemaVer);
            Update(connection, transaction, id, title, body, payloadJson, tagsJson, sourceAgent, schemaVer, nowUtc, contentHash, searchStems, project);
        }

        var after = NoteAudit.Snapshot(title, body, payloadJson, tagsJson, existing?.Status ?? "active", schemaVer);
        var op = created ? "create" : "update";
        NoteAudit.Append(connection, transaction, id, op, sourceAgent, nowUtc, NoteAudit.BuildDiff(op, before, after));
        return (id, created);
    }

    /// <summary>
    /// Creates (or dedup-upserts) a note AND its outgoing links in ONE transaction — all-or-nothing.
    /// If any link's target is missing or its relation is invalid, nothing is persisted (no half-built case).
    /// </summary>
    public AssembleResult Assemble(
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey,
        IReadOnlyList<AssembleLink>? links, string? sourceAgent)
    {
        domain = Identifiers.Normalize(domain);
        type = Identifiers.Normalize(type);
        tagsJson = Identifiers.NormalizeTags(tagsJson);

        var validation = _validator.Validate(type, payloadJson ?? "{}");
        if (!validation.IsValid)
        {
            throw new NoteValidationException(validation.Errors);
        }

        var schemaVer = _registry.GetLatest(type)!.Version;
        var nowUtc = NowUtc();
        var specs = links ?? Array.Empty<AssembleLink>();

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        var (id, created) = PersistNote(connection, transaction, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc);

        foreach (var link in specs)
        {
            if (!RelationName.IsValid(link.Rel))
            {
                throw new AssembleException($"Invalid relation '{link.Rel}': {RelationName.Expectation}.");
            }

            if (!NoteExists(connection, transaction, link.ToId))
            {
                throw new AssembleException($"Link target '{link.ToId}' does not exist; nothing was created.");
            }

            InsertLinkRow(connection, transaction, id, link.ToId, link.Rel, nowUtc);
        }

        transaction.Commit();
        return new AssembleResult(id, created, nowUtc, specs.Count);
    }

    private static bool NoteExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM notes WHERE id = $id AND deleted = 0);";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static void InsertLinkRow(SqliteConnection connection, SqliteTransaction transaction, string fromId, string toId, string rel, string nowUtc)
    {
        int inserted;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO note_links (from_id, to_id, rel, created_utc) VALUES ($f, $t, $r, $now);";
            command.Parameters.AddWithValue("$f", fromId);
            command.Parameters.AddWithValue("$t", toId);
            command.Parameters.AddWithValue("$r", rel);
            command.Parameters.AddWithValue("$now", nowUtc);
            inserted = command.ExecuteNonQuery();
        }

        if (inserted == 0)
        {
            return; // the link already existed (idempotent) — no new edge, no audit event
        }

        NoteAudit.Append(connection, transaction, fromId, "link", null, nowUtc,
            new JsonObject { ["op"] = "link", ["to"] = toId, ["rel"] = rel }.ToJsonString());
    }

    /// <summary>
    /// Appends a schema-less free-text journal note (type <c>journal</c>) and audits it. Capture-first for
    /// raw human notes; structuring happens later. A title is derived from the first line when not given,
    /// a stable dedupKey is assigned (so it is findable/editable), and the note is tagged
    /// <c>unstructured</c> so it can be located for later structuring.
    /// </summary>
    /// <param name="domain">Namespace, e.g. <c>kitchen</c>.</param>
    /// <param name="text">The raw free-text content (stored as the body).</param>
    /// <param name="title">Optional title; when null/blank it is derived from the first line of the text.</param>
    /// <param name="tagsJson">Optional tags (JSON array string); <c>unstructured</c> is always added.</param>
    /// <param name="sourceAgent">Provenance: who is writing.</param>
    /// <returns>The new note id.</returns>
    public string AppendJournal(string domain, string text, string? title = null, string? tagsJson = null, string? sourceAgent = null)
    {
        domain = Identifiers.Normalize(domain);
        var nowUtc = NowUtc();
        var id = Guid.NewGuid().ToString("N");
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? DeriveTitle(text) : title.Trim();
        var tags = MergeUnstructuredTag(tagsJson);
        var dedupKey = "journal-" + id[..8];

        var contentHash = ContentHash.Compute("journal", resolvedTitle, text, null, tags);
        var searchStems = SearchStems.For(resolvedTitle, text, tags, null);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        Insert(connection, transaction, id, domain, "journal", resolvedTitle, text, null, tags, dedupKey, sourceAgent, 0, nowUtc, contentHash, searchStems);
        NoteAudit.Append(connection, transaction, id, "create", sourceAgent, nowUtc,
            new JsonObject { ["op"] = "create", ["type"] = "journal" }.ToJsonString());
        transaction.Commit();
        return id;
    }

    // First non-empty line (sans leading Markdown #), trimmed to a reasonable title length.
    private static string DeriveTitle(string text)
    {
        var firstLine = (text ?? string.Empty).Split('\n')
            .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        firstLine = firstLine.TrimStart('#', ' ').Trim();
        if (firstLine.Length == 0)
        {
            return "journal";
        }

        return firstLine.Length <= 80 ? firstLine : firstLine[..79] + "...";
    }

    // Normalizes the supplied tags and guarantees the 'unstructured' marker is present.
    private static string MergeUnstructuredTag(string? tagsJson)
    {
        JsonArray array;
        try
        {
            array = JsonNode.Parse(Identifiers.NormalizeTags(tagsJson) ?? "[]") as JsonArray ?? new JsonArray();
        }
        catch (System.Text.Json.JsonException)
        {
            array = new JsonArray();
        }

        if (!array.Any(node => node is JsonValue value && value.TryGetValue<string>(out var tag) && tag == "unstructured"))
        {
            array.Add("unstructured");
        }

        return array.ToJsonString();
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
        if (!NoteExists(connection, transaction, fromId))
        {
            throw new AssembleException($"Link source '{fromId}' does not exist.");
        }

        if (!NoteExists(connection, transaction, toId))
        {
            throw new AssembleException($"Link target '{toId}' does not exist.");
        }

        InsertLinkRow(connection, transaction, fromId, toId, rel, nowUtc);
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

    /// <summary>
    /// Partially updates a note by id (merge semantics): only the supplied envelope fields change, and a
    /// supplied <paramref name="payloadJson"/> is shallow-merged into the existing payload (its keys win).
    /// If <paramref name="expectedUpdatedUtc"/> is given and no longer matches, throws
    /// <see cref="ConcurrencyException"/> (optimistic concurrency). Returns the updated note, or null if
    /// no active note has that id. The merged payload is validated; an invalid result throws and writes nothing.
    /// </summary>
    public Note? Patch(string id, string? title, string? body, string? payloadJson, string? tagsJson,
        string? expectedUpdatedUtc, string? sourceAgent)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var current = ReadForPatch(connection, transaction, id);
        if (current is null)
        {
            return null;
        }

        if (expectedUpdatedUtc is not null && !string.Equals(expectedUpdatedUtc, current.UpdatedUtc, StringComparison.Ordinal))
        {
            throw new ConcurrencyException(
                $"Stale update: this note changed since you read it (current revision {current.UpdatedUtc}). Re-read and retry.");
        }

        var newTitle = title ?? current.Title;
        var newBody = body ?? current.Body;
        var newTags = tagsJson ?? current.Tags;
        var newPayload = MergePayload(current.Payload, payloadJson);

        if (_registry.GetLatest(current.Type) is not null)
        {
            var validation = _validator.Validate(current.Type, newPayload ?? "{}");
            if (!validation.IsValid)
            {
                throw new NoteValidationException(validation.Errors);
            }
        }

        var before = NoteAudit.Snapshot(current.Title, current.Body, current.Payload, current.Tags, current.Status, current.SchemaVer);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE notes SET title = $t, body = $b, payload_json = $p, tags_json = $g, updated_utc = $now, source_agent = $by WHERE id = $id;";
            update.Parameters.AddWithValue("$t", (object?)newTitle ?? DBNull.Value);
            update.Parameters.AddWithValue("$b", (object?)newBody ?? DBNull.Value);
            update.Parameters.AddWithValue("$p", (object?)newPayload ?? DBNull.Value);
            update.Parameters.AddWithValue("$g", (object?)newTags ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", nowUtc);
            update.Parameters.AddWithValue("$by", (object?)sourceAgent ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }

        var after = NoteAudit.Snapshot(newTitle, newBody, newPayload, newTags, current.Status, current.SchemaVer);
        NoteAudit.Append(connection, transaction, id, "patch", sourceAgent, nowUtc, NoteAudit.BuildDiff("patch", before, after));
        transaction.Commit();

        return new Note(id, current.Domain, current.Type, newTitle, newBody, newPayload, newTags,
            current.DedupKey, current.Status, current.CreatedUtc, nowUtc, sourceAgent, current.SchemaVer, false);
    }

    private static PatchSource? ReadForPatch(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            "SELECT domain, type, title, body, payload_json, tags_json, dedup_key, status, created_utc, updated_utc, schema_ver " +
            "FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
        select.Parameters.AddWithValue("$id", id);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PatchSource(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetInt32(10));
    }

    private sealed record PatchSource(string Domain, string Type, string? Title, string? Body, string? Payload,
        string? Tags, string? DedupKey, string Status, string CreatedUtc, string UpdatedUtc, int SchemaVer);

    // Shallow-merges a JSON patch object into the current payload (patch keys win). Null patch -> unchanged.
    private static string? MergePayload(string? current, string? patch)
    {
        if (patch is null)
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return patch;
        }

        if (JsonNode.Parse(current) is not JsonObject baseObject || JsonNode.Parse(patch) is not JsonObject patchObject)
        {
            return patch; // non-object payloads: replace rather than merge
        }

        foreach (var pair in patchObject)
        {
            baseObject[pair.Key] = pair.Value?.DeepClone();
        }

        return baseObject.ToJsonString();
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
        int schemaVer, string nowUtc, string contentHash, string? searchStems, string? project = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO notes (id, domain, project, type, title, body, payload_json, tags_json, dedup_key, status, " +
            "created_utc, updated_utc, source_agent, schema_ver, deleted, content_hash, search_stems) " +
            "VALUES ($id, $d, $proj, $ty, $title, $body, $payload, $tags, $dedup, 'active', $now, $now, $agent, $ver, 0, $hash, $stems);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$d", domain);
        AddNullable(command, "$proj", project);
        command.Parameters.AddWithValue("$ty", type);
        AddNullable(command, "$title", title);
        AddNullable(command, "$body", body);
        AddNullable(command, "$payload", payloadJson);
        AddNullable(command, "$tags", tagsJson);
        AddNullable(command, "$dedup", dedupKey);
        AddNullable(command, "$agent", sourceAgent);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$ver", schemaVer);
        command.Parameters.AddWithValue("$hash", contentHash);
        AddNullable(command, "$stems", searchStems);
        command.ExecuteNonQuery();
    }

    private static void Update(
        SqliteConnection connection, SqliteTransaction transaction, string id,
        string? title, string? body, string? payloadJson, string? tagsJson, string? sourceAgent, int schemaVer, string nowUtc, string contentHash, string? searchStems, string? project = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // COALESCE keeps the existing project when the caller doesn't supply one (a plain re-upsert won't null it).
        command.CommandText =
            "UPDATE notes SET title = $title, body = $body, payload_json = $payload, tags_json = $tags, " +
            "source_agent = $agent, schema_ver = $ver, updated_utc = $now, content_hash = $hash, search_stems = $stems, " +
            "project = COALESCE($proj, project) WHERE id = $id;";
        AddNullable(command, "$title", title);
        AddNullable(command, "$body", body);
        AddNullable(command, "$payload", payloadJson);
        AddNullable(command, "$tags", tagsJson);
        AddNullable(command, "$agent", sourceAgent);
        AddNullable(command, "$proj", project);
        command.Parameters.AddWithValue("$ver", schemaVer);
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$hash", contentHash);
        AddNullable(command, "$stems", searchStems);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    // Reads a string "project" from the typed payload, if present (used to seed the envelope project axis).
    private static string? PayloadProject(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payloadJson) is JsonObject obj && obj["project"] is JsonValue value
                && value.TryGetValue(out string? project) ? project : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record ExistingNote(
        string Id, string? Title, string? Body, string? PayloadJson, string? TagsJson, string Status, int SchemaVer);
}
