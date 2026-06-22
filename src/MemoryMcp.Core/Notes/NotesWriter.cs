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
    private readonly INoteEventSink _eventSink;

    /// <summary>Creates a writer over the given database, schema registry and clock.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="registry">Schema registry used for validation and version stamping.</param>
    /// <param name="timeProvider">Clock for timestamps; defaults to the system clock.</param>
    /// <param name="eventSink">Sink for post-commit note-change events; defaults to a no-op sink.</param>
    public NotesWriter(ISqliteConnectionFactory connectionFactory, SchemaRegistry registry, TimeProvider? timeProvider = null, INoteEventSink? eventSink = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _validator = new SchemaValidator(registry);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _eventSink = eventSink ?? NullNoteEventSink.Instance;
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
    /// <param name="expectedRevision">Optional optimistic-concurrency guard: the prior <c>updated_utc</c>; when given,
    /// the dedup-matched note is updated only if its revision still matches, else <see cref="ConcurrencyException"/>.</param>
    /// <returns>The note id and whether it was created.</returns>
    public UpsertResult Upsert(
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent, string? project = null,
        string? expectedRevision = null)
    {
        domain = Identifiers.Normalize(domain);
        type = Identifiers.Normalize(type);
        tagsJson = Identifiers.NormalizeTags(tagsJson);
        // Fall back to payload.project when no explicit envelope project is given, so a note that only carries
        // its project in the typed payload (e.g. backlog_item) still gets the envelope axis set.
        project = Identifiers.NormalizeOptional(project ?? PayloadProject(payloadJson));

        var validation = _validator.Validate(type, PayloadForValidation(payloadJson));
        if (!validation.IsValid)
        {
            throw new NoteValidationException(validation.Errors);
        }

        var schemaVer = _registry.GetLatest(type)!.Version;
        var nowUtc = NowUtc();

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        var (id, created, unchanged, revision) = PersistNote(connection, transaction, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc, project, expectedRevision);
        transaction.Commit();
        if (!unchanged)
        {
            Emit(id, domain, type, project, tagsJson, created ? "create" : "update", nowUtc);
        }

        return new UpsertResult(id, created, revision, type, dedupKey, unchanged);
    }

    /// <summary>Largest number of items a single bulk upsert/link may carry, so a batch can't run unbounded.</summary>
    public const int MaxBatch = 100;

    /// <summary>
    /// Upserts many notes in ONE transaction, all-or-nothing (MEMP-180): every payload is validated up front, so an
    /// invalid item (or a concurrency conflict) aborts the whole batch — the error names the offending index and
    /// nothing is persisted. On success, returns one <see cref="UpsertResult"/> per input (in order). Events are
    /// emitted after commit for the items that actually changed.
    /// </summary>
    /// <param name="inputs">The notes to upsert (≤ <see cref="MaxBatch"/>).</param>
    /// <param name="sourceAgent">Provenance: who is writing.</param>
    public IReadOnlyList<UpsertResult> UpsertMany(IReadOnlyList<NoteUpsertInput> inputs, string? sourceAgent)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<UpsertResult>();
        }

        if (inputs.Count > MaxBatch)
        {
            throw new AssembleException($"Too many items: {inputs.Count} exceeds the {MaxBatch} per-batch limit.");
        }

        var nowUtc = NowUtc();
        var prepared = PrepareBatch(inputs); // validate every item up front, before any write
        var results = new List<UpsertResult>(inputs.Count);
        var pendingEmits = new List<(string Id, string Domain, string Type, string? Project, string? Tags, string Op)>();
        using (var connection = _connectionFactory.Create())
        using (var transaction = connection.BeginTransaction())
        {
            for (var i = 0; i < prepared.Count; i++)
            {
                var item = prepared[i];
                var (id, created, unchanged, revision) = PersistBatchItem(connection, transaction, item, sourceAgent, nowUtc, i);
                results.Add(new UpsertResult(id, created, revision, item.Type, item.Input.DedupKey, unchanged));
                if (!unchanged)
                {
                    pendingEmits.Add((id, item.Domain, item.Type, item.Project, item.Tags, created ? "create" : "update"));
                }
            }

            transaction.Commit();
        }

        foreach (var emit in pendingEmits)
        {
            Emit(emit.Id, emit.Domain, emit.Type, emit.Project, emit.Tags, emit.Op, nowUtc);
        }

        return results;
    }

    // Normalizes and schema-validates every batch item before any write, so an invalid item aborts the whole batch.
    private List<PreparedUpsert> PrepareBatch(IReadOnlyList<NoteUpsertInput> inputs)
    {
        var prepared = new List<PreparedUpsert>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var domain = Identifiers.Normalize(input.Domain);
            var type = Identifiers.Normalize(input.Type);
            var tags = Identifiers.NormalizeTags(input.TagsJson);
            var project = Identifiers.NormalizeOptional(input.Project ?? PayloadProject(input.PayloadJson));

            var validation = _validator.Validate(type, PayloadForValidation(input.PayloadJson));
            if (!validation.IsValid)
            {
                throw new NoteValidationException(validation.Errors.Select(error => $"item[{i}] ({domain}/{type}): {error}").ToList());
            }

            prepared.Add(new PreparedUpsert(input, domain, type, tags, project, _registry.GetLatest(type)!.Version));
        }

        return prepared;
    }

    // Persists one prepared batch item, tagging any concurrency conflict with its index for a readable error.
    private static (string Id, bool Created, bool Unchanged, string Revision) PersistBatchItem(
        SqliteConnection connection, SqliteTransaction transaction, PreparedUpsert item, string? sourceAgent, string nowUtc, int index)
    {
        try
        {
            return PersistNote(connection, transaction, item.Domain, item.Type, item.Input.Title, item.Input.Body,
                item.Input.PayloadJson, item.Tags, item.Input.DedupKey, sourceAgent, item.SchemaVer, nowUtc, item.Project, item.Input.ExpectedRevision);
        }
        catch (ConcurrencyException conflict)
        {
            throw new ConcurrencyException($"item[{index}]: {conflict.Message}", conflict.CurrentRevision, conflict.CurrentSourceAgent, conflict.ChangedFields);
        }
    }

    private sealed record PreparedUpsert(NoteUpsertInput Input, string Domain, string Type, string? Tags, string? Project, int SchemaVer);

    // Inserts a new note or dedup-updates the existing one, plus its audit event, within an existing
    // transaction (the caller commits). Shared by Upsert and Assemble. When expectedRevision is given it is an
    // optimistic-concurrency guard (MEMP-179); an update whose new content equals the stored content is a no-op
    // (MEMP-183) — no row write, no audit event — and Revision returns the unchanged revision.
    private static (string Id, bool Created, bool Unchanged, string Revision) PersistNote(
        SqliteConnection connection, SqliteTransaction transaction,
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent, int schemaVer, string nowUtc,
        string? project = null, string? expectedRevision = null)
    {
        var existing = dedupKey is null ? null : FindByDedup(connection, transaction, domain, type, dedupKey);

        var contentHash = ContentHash.Compute(type, title, body, payloadJson, tagsJson);
        var searchStems = SearchStems.For(title, body, tagsJson, payloadJson);

        if (existing is null)
        {
            if (expectedRevision is not null)
            {
                throw ConcurrencyException.Missing(); // caller expected to update an existing note, but there is none
            }

            var newId = Guid.NewGuid().ToString("N");
            Insert(connection, transaction, newId, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc, contentHash, searchStems, project);
            var createdAfter = NoteAudit.Snapshot(title, body, payloadJson, tagsJson, "active", schemaVer);
            NoteAudit.Append(connection, transaction, newId, "create", sourceAgent, nowUtc, NoteAudit.BuildDiff("create", null, createdAfter));
            return (newId, true, false, nowUtc);
        }

        if (expectedRevision is not null && !string.Equals(expectedRevision, existing.UpdatedUtc, StringComparison.Ordinal))
        {
            throw ConcurrencyException.Stale(existing.UpdatedUtc, existing.SourceAgent,
                ChangedFields(existing.Title, existing.Body, existing.PayloadJson, existing.TagsJson, title, body, payloadJson, tagsJson));
        }

        // Identical content (and project unchanged) → a quiet no-op: don't bump the revision or emit an event.
        var projectUnchanged = project is null || string.Equals(project, existing.Project, StringComparison.Ordinal);
        if (projectUnchanged && string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return (existing.Id, false, true, existing.UpdatedUtc);
        }

        var before = NoteAudit.Snapshot(existing.Title, existing.Body, existing.PayloadJson, existing.TagsJson, existing.Status, existing.SchemaVer);
        Update(connection, transaction, existing.Id, title, body, payloadJson, tagsJson, sourceAgent, schemaVer, nowUtc, contentHash, searchStems, project);
        var after = NoteAudit.Snapshot(title, body, payloadJson, tagsJson, existing.Status, schemaVer);
        NoteAudit.Append(connection, transaction, existing.Id, "update", sourceAgent, nowUtc, NoteAudit.BuildDiff("update", before, after));
        return (existing.Id, false, false, nowUtc);
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

        var validation = _validator.Validate(type, PayloadForValidation(payloadJson));
        if (!validation.IsValid)
        {
            throw new NoteValidationException(validation.Errors);
        }

        var schemaVer = _registry.GetLatest(type)!.Version;
        var nowUtc = NowUtc();
        var specs = links ?? Array.Empty<AssembleLink>();

        var project = Identifiers.NormalizeOptional(PayloadProject(payloadJson)); // lift the project axis from payload (MEMP-154)
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        var (id, created, _, _) = PersistNote(connection, transaction, domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent, schemaVer, nowUtc, project);

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
        // Read the EFFECTIVE project back: on a dedup-update with no payload.project, the stored value is the
        // COALESCE-preserved existing one, not the (null) derived `project` — so echo/emit the real value.
        var effectiveProject = ReadNoteProject(connection, id);
        Emit(id, domain, type, effectiveProject, tagsJson, created ? "create" : "update", nowUtc);
        return new AssembleResult(id, created, nowUtc, specs.Count, effectiveProject);
    }

    // Reads a note's current envelope project (used to echo the effective scope after a write).
    private static string? ReadNoteProject(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT project FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static bool NoteExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM notes WHERE id = $id AND deleted = 0);";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    // Inserts a directed link (idempotent) and audits it; returns true when a new edge was created, false when it
    // already existed. Callers that don't care about the distinction can ignore the result.
    private static bool InsertLinkRow(SqliteConnection connection, SqliteTransaction transaction, string fromId, string toId, string rel, string nowUtc)
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
            return false; // the link already existed (idempotent) — no new edge, no audit event
        }

        NoteAudit.Append(connection, transaction, fromId, "link", null, nowUtc,
            new JsonObject { ["op"] = "link", ["to"] = toId, ["rel"] = rel }.ToJsonString());
        return true;
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
        Emit(id, domain, "journal", null, tags, "create", nowUtc);
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

    /// <summary>
    /// Creates many directed links in ONE transaction, all-or-nothing (MEMP-181): every relation is validated and
    /// every endpoint must exist, else nothing is persisted and the error names the offending index. Idempotent —
    /// a duplicate edge is a no-op. Returns how many edges were newly created vs already present.
    /// </summary>
    /// <param name="links">The edges to create (≤ <see cref="MaxBatch"/>).</param>
    public LinkManyResult LinkMany(IReadOnlyList<LinkInput> links)
    {
        if (links.Count == 0)
        {
            return new LinkManyResult(0, 0);
        }

        if (links.Count > MaxBatch)
        {
            throw new AssembleException($"Too many links: {links.Count} exceeds the {MaxBatch} per-batch limit.");
        }

        var nowUtc = NowUtc();
        var created = 0;
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (!RelationName.IsValid(link.Rel))
            {
                throw new AssembleException($"link[{i}]: invalid relation '{link.Rel}': {RelationName.Expectation}.");
            }

            if (!NoteExists(connection, transaction, link.FromId))
            {
                throw new AssembleException($"link[{i}]: source '{link.FromId}' does not exist; nothing was created.");
            }

            if (!NoteExists(connection, transaction, link.ToId))
            {
                throw new AssembleException($"link[{i}]: target '{link.ToId}' does not exist; nothing was created.");
            }

            if (InsertLinkRow(connection, transaction, link.FromId, link.ToId, link.Rel, nowUtc))
            {
                created++;
            }
        }

        transaction.Commit();
        return new LinkManyResult(created, links.Count - created);
    }

    /// <summary>Soft-archives a note (status = archived). Returns false if no active note has that id.</summary>
    /// <param name="id">The note id.</param>
    public bool Archive(string id)
    {
        var nowUtc = NowUtc();
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var facets = ReadFacets(connection, transaction, id);
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
        Emit(facets, id, "archive", nowUtc);
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

        var facets = ReadFacets(connection, transaction, oldId);
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
        Emit(facets, oldId, "supersede", nowUtc);
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

        var newTitle = title ?? current.Title;
        var newBody = body ?? current.Body;
        var newTags = tagsJson ?? current.Tags;
        var newPayload = MergePayload(current.Payload, payloadJson);

        if (expectedUpdatedUtc is not null && !string.Equals(expectedUpdatedUtc, current.UpdatedUtc, StringComparison.Ordinal))
        {
            throw ConcurrencyException.Stale(current.UpdatedUtc, current.SourceAgent,
                ChangedFields(current.Title, current.Body, current.Payload, current.Tags, newTitle, newBody, newPayload, newTags));
        }

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
            current.DedupKey, current.Status, current.CreatedUtc, nowUtc, sourceAgent, current.SchemaVer, false, current.Project);
    }

    private static PatchSource? ReadForPatch(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            "SELECT domain, type, title, body, payload_json, tags_json, dedup_key, status, created_utc, updated_utc, schema_ver, source_agent, project " +
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
            reader.GetString(8), reader.GetString(9), reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private sealed record PatchSource(string Domain, string Type, string? Title, string? Body, string? Payload,
        string? Tags, string? DedupKey, string Status, string CreatedUtc, string UpdatedUtc, int SchemaVer, string? SourceAgent, string? Project);

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

    // Best-effort post-commit notification. NEVER lets a sink failure escape into the write path.
    private void Emit(string id, string domain, string type, string? project, string? tagsJson, string op, string nowUtc)
    {
        try
        {
            _eventSink.Publish(new NoteChangedEvent(id, domain, type, project, ParseTags(tagsJson), op, nowUtc));
        }
        catch
        {
            // sinks are best-effort: a broker hiccup must not fail (or roll back) a committed write
        }
    }

    private void Emit(NoteFacets? facets, string id, string op, string nowUtc)
    {
        if (facets is null)
        {
            return; // the row vanished between read and commit; nothing meaningful to publish
        }

        Emit(id, facets.Domain, facets.Type, facets.Project, facets.TagsJson, op, nowUtc);
    }

    // Reads the envelope facets needed for a change event, inside the active transaction. Null if the row is gone.
    private static NoteFacets? ReadFacets(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT domain, type, project, tags_json FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new NoteFacets(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    // Parses a tags_json array into a string list; empty on null/blank/malformed input.
    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            if (JsonNode.Parse(tagsJson) is not JsonArray array)
            {
                return Array.Empty<string>();
            }

            var tags = new List<string>(array.Count);
            foreach (var node in array)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var tag))
                {
                    tags.Add(tag);
                }
            }

            return tags;
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private sealed record NoteFacets(string Domain, string Type, string? Project, string? TagsJson);

    private static ExistingNote? FindByDedup(SqliteConnection connection, SqliteTransaction transaction, string domain, string type, string dedupKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, title, body, payload_json, tags_json, status, schema_ver, updated_utc, content_hash, project, source_agent " +
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
            reader.GetInt32(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
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

    /// <summary>True if <paramref name="payloadJson"/> is valid for <paramref name="type"/> (project-axis aware).
    /// Used for import dry-runs to report would-be-invalid notes without writing.</summary>
    /// <param name="type">The note type.</param>
    /// <param name="payloadJson">The payload to check.</param>
    public bool IsValidPayload(string type, string? payloadJson) =>
        _validator.Validate(Identifiers.Normalize(type), PayloadForValidation(payloadJson)).IsValid;

    // Payload to validate against the type schema, with a top-level "project" lifted out (MEMP-154): project is an
    // ENVELOPE axis, so any type may carry payload.project to set it — even types whose schema forbids extra fields
    // (e.g. reference). The original payload is still stored as-is; only the schema check sees the stripped copy.
    private static string PayloadForValidation(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "{}";
        }

        try
        {
            if (JsonNode.Parse(payloadJson) is JsonObject obj && obj.ContainsKey("project"))
            {
                obj.Remove("project");
                return obj.ToJsonString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through: let the validator surface the malformed-JSON error
        }

        return payloadJson;
    }

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
        string Id, string? Title, string? Body, string? PayloadJson, string? TagsJson, string Status, int SchemaVer,
        string UpdatedUtc, string? ContentHash, string? Project, string? SourceAgent);

    // Fields among {title,body,payload,tags} where an incoming write differs from the current copy (conflict hint, MEMP-182).
    private static IReadOnlyList<string> ChangedFields(string? curTitle, string? curBody, string? curPayload, string? curTags,
        string? newTitle, string? newBody, string? newPayload, string? newTags)
    {
        var changed = new List<string>(4);
        if (!string.Equals(curTitle, newTitle, StringComparison.Ordinal))
        {
            changed.Add("title");
        }

        if (!string.Equals(curBody, newBody, StringComparison.Ordinal))
        {
            changed.Add("body");
        }

        if (!string.Equals(curPayload, newPayload, StringComparison.Ordinal))
        {
            changed.Add("payload");
        }

        if (!string.Equals(curTags, newTags, StringComparison.Ordinal))
        {
            changed.Add("tags");
        }

        return changed;
    }
}
