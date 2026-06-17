using System.Text.Json;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

/// <summary>Read-side note access: get by id, list, and paginated full-text / structured search.</summary>
public sealed class NotesReader
{
    /// <summary>Largest page a single search may return, so a caller can't pull the whole store at once.</summary>
    public const int MaxLimit = 100;

    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultLimit = 20;

    /// <summary>Default number of body characters a single <see cref="ReadBody"/> returns.</summary>
    public const int DefaultReadChars = 4000;

    /// <summary>Largest body slice a single <see cref="ReadBody"/> may return.</summary>
    public const int MaxReadChars = 20000;

    /// <summary>Default characters of context on each side of an in-note <see cref="Find"/> match.</summary>
    public const int DefaultContextChars = 80;

    /// <summary>Default number of matches a single <see cref="Find"/> returns.</summary>
    public const int DefaultFindMatches = 10;

    private readonly ISqliteConnectionFactory _connectionFactory;

    /// <summary>Creates a reader over the given database.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    public NotesReader(ISqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <summary>Returns the full note by id, or <c>null</c> if it does not exist.</summary>
    /// <param name="id">The note id.</param>
    public Note? Get(string id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NoteRowMapper.Columns} FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? NoteRowMapper.Map(reader) : null;
    }

    /// <summary>Active note counts grouped by type within a single domain (powers the domain manifest).</summary>
    /// <param name="domain">The domain (namespace) to count within.</param>
    public IReadOnlyDictionary<string, long> CountByTypeInDomain(string domain)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, count(*) FROM notes WHERE deleted = 0 AND status = 'active' AND domain = $d GROUP BY type ORDER BY type;";
        command.Parameters.AddWithValue("$d", Identifiers.Normalize(domain));

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    /// <summary>
    /// Returns a note plus cheap size metadata, applying body windowing so a caller need not pull a huge
    /// body to inspect a note. <paramref name="includeBody"/>=false omits the body; <paramref name="bodyMaxChars"/>
    /// (when set) caps it; either way <see cref="NoteView.BodyChars"/> reports the true length.
    /// </summary>
    /// <param name="id">The note id.</param>
    /// <param name="includeBody">When false, the body is omitted (peek).</param>
    /// <param name="bodyMaxChars">When set (>=0), caps the returned body to this many characters; null = full body.</param>
    public NoteView? GetView(string id, bool includeBody = true, int? bodyMaxChars = null)
    {
        var note = Get(id);
        if (note is null)
        {
            return null;
        }

        var (attachments, links) = Counts(id);
        return NoteView.From(note, includeBody, bodyMaxChars, attachments, links);
    }

    // Counts the artifacts attached to a note and the links touching it (both directions).
    private (int Attachments, int Links) Counts(string id)
    {
        using var connection = _connectionFactory.Create();

        int attachments;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM attachments WHERE note_id = $id;";
            command.Parameters.AddWithValue("$id", id);
            attachments = Convert.ToInt32(command.ExecuteScalar());
        }

        int links;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM note_links WHERE from_id = $id OR to_id = $id;";
            command.Parameters.AddWithValue("$id", id);
            links = Convert.ToInt32(command.ExecuteScalar());
        }

        return (attachments, links);
    }

    /// <summary>
    /// Reads a window of a note's body: from <paramref name="offset"/> for up to <paramref name="limitChars"/>
    /// characters, with the partial-read contract (total/returned/truncated/nextOffset/revision). Returns
    /// <c>null</c> only when the note does not exist; an empty body yields an empty slice.
    /// </summary>
    /// <param name="id">The note id.</param>
    /// <param name="offset">Start character offset (clamped to [0, totalChars]).</param>
    /// <param name="limitChars">Max characters to return (clamped to [1, <see cref="MaxReadChars"/>]).</param>
    public NoteReadSlice? ReadBody(string id, int offset = 0, int limitChars = DefaultReadChars)
    {
        var note = Get(id);
        if (note is null)
        {
            return null;
        }

        var body = note.Body ?? string.Empty;
        var total = body.Length;
        var start = Math.Clamp(offset, 0, total);
        var take = Math.Clamp(limitChars, 1, MaxReadChars);
        var end = Math.Min(start + take, total);
        var content = body.Substring(start, end - start);
        var truncated = end < total;
        return new NoteReadSlice(id, content, start, content.Length, total, truncated, truncated ? end : null, note.UpdatedUtc);
    }

    /// <summary>
    /// Finds occurrences of <paramref name="query"/> in a note's body (case-insensitive), each with a
    /// surrounding context window and offset — so a caller can locate and read just the relevant parts of a
    /// large note. Returns <c>null</c> only when the note is missing.
    /// </summary>
    /// <param name="id">The note id.</param>
    /// <param name="query">Substring to search for; empty yields no matches.</param>
    /// <param name="contextChars">Characters of context each side of a match (clamped to [0, 500]).</param>
    /// <param name="limit">Max matches to return (clamped to [1, 100]); more set <c>Truncated</c>.</param>
    public NoteFindResult? Find(string id, string query, int contextChars = DefaultContextChars, int limit = DefaultFindMatches)
    {
        var note = Get(id);
        if (note is null)
        {
            return null;
        }

        var body = note.Body ?? string.Empty;
        var matches = new List<NoteMatch>();
        var count = 0;
        if (!string.IsNullOrEmpty(query) && query.Length <= body.Length)
        {
            var context = Math.Clamp(contextChars, 0, 500);
            var max = Math.Clamp(limit, 1, 100);
            var pos = 0;
            int idx;
            while (pos <= body.Length - query.Length && (idx = body.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                if (matches.Count < max)
                {
                    var start = Math.Max(0, idx - context);
                    var end = Math.Min(body.Length, idx + query.Length + context);
                    matches.Add(new NoteMatch(idx, body.Substring(start, end - start)));
                }

                pos = idx + query.Length;
            }
        }

        return new NoteFindResult(id, query, body.Length, count, count > matches.Count, matches, note.UpdatedUtc);
    }

    /// <summary>Returns the Markdown heading map of a note's body (for section reads), or <c>null</c> if missing.</summary>
    /// <param name="id">The note id.</param>
    public NoteOutline? Outline(string id)
    {
        var note = Get(id);
        return note is null ? null : new NoteOutline(id, note.Body?.Length ?? 0, note.UpdatedUtc, MarkdownOutline.Parse(note.Body));
    }

    /// <summary>Returns the active note for (domain, type, dedupKey), or <c>null</c>. Key-addressed lookup (e.g. skills).</summary>
    /// <param name="domain">Domain filter.</param>
    /// <param name="type">Type filter.</param>
    /// <param name="dedupKey">Stable upsert key.</param>
    public Note? GetByDedupKey(string domain, string type, string dedupKey)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {NoteRowMapper.Columns} FROM notes " +
            "WHERE domain = $d AND type = $t AND dedup_key = $k AND deleted = 0 AND status = 'active' LIMIT 1;";
        command.Parameters.AddWithValue("$d", Identifiers.Normalize(domain));
        command.Parameters.AddWithValue("$t", Identifiers.Normalize(type));
        command.Parameters.AddWithValue("$k", dedupKey);

        using var reader = command.ExecuteReader();
        return reader.Read() ? NoteRowMapper.Map(reader) : null;
    }

    /// <summary>Returns distinct tags across live notes with their counts (facet discovery), most-used first.</summary>
    public IReadOnlyDictionary<string, long> TagCounts()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT json_each.value AS tag, count(*) AS n FROM notes, json_each(notes.tags_json) " +
            "WHERE notes.deleted = 0 AND notes.tags_json IS NOT NULL GROUP BY tag ORDER BY n DESC, tag;";

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    /// <summary>
    /// Recalls a compact context block for a query: the primary search hits plus their one-hop linked
    /// neighbors (both directions), all scope-restricted. Reuses FTS search + the link graph — no vectors.
    /// </summary>
    /// <param name="query">Full-text query (optional).</param>
    /// <param name="domain">Optional domain filter.</param>
    /// <param name="limit">Max primary hits (page size).</param>
    /// <param name="restrictToDomains">Auth scope; restricts both hits and neighbors.</param>
    /// <param name="includeLinks">When true, include one-hop linked neighbors of the hits.</param>
    /// <param name="maxHops">Graph expansion depth; currently one hop (values &gt; 1 behave as 1).</param>
    public RecallResult Recall(
        string? query, string? domain, int limit, IReadOnlyCollection<string>? restrictToDomains,
        bool includeLinks = true, int maxHops = 1)
    {
        var page = Search(query, domain, null, null, "active", limit, 0, restrictToDomains, null, includePayload: true);
        var hits = page.Items;

        var neighbors = new List<RecallNeighbor>();
        if (includeLinks && maxHops >= 1 && hits.Count > 0)
        {
            var seen = new HashSet<string>(hits.Select(hit => hit.Id), StringComparer.Ordinal);
            foreach (var hit in hits)
            {
                foreach (var link in Links(hit.Id))
                {
                    if (!seen.Add(link.NoteId))
                    {
                        continue; // already a hit or an earlier neighbor
                    }

                    if (restrictToDomains is not null && !restrictToDomains.Contains(link.Domain))
                    {
                        continue; // neighbor outside the caller's scope
                    }

                    neighbors.Add(new RecallNeighbor(link.NoteId, link.Title, link.Type, link.Domain, link.Rel, link.Direction, hit.Id));
                }
            }
        }

        return new RecallResult(query, hits, neighbors);
    }

    /// <summary>
    /// Lists the most-recently-updated notes (or most-used when <paramref name="byUsage"/>), scope-restricted,
    /// with payload — so an agent can see what's already there and avoid duplicates. Snippet/score are null.
    /// </summary>
    /// <param name="domain">Optional domain filter.</param>
    /// <param name="type">Optional type filter.</param>
    /// <param name="limit">Max results (clamped to [1, 100]).</param>
    /// <param name="restrictToDomains">Auth scope; null = unrestricted.</param>
    /// <param name="byUsage">Sort by access count/recency (from note_usage) instead of last update.</param>
    public IReadOnlyList<SearchResult> Recent(string? domain, string? type, int limit, IReadOnlyCollection<string>? restrictToDomains, bool byUsage)
    {
        domain = Identifiers.NormalizeOptional(domain);
        type = Identifiers.NormalizeOptional(type);
        limit = Math.Clamp(limit, 1, MaxLimit);

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var filters = new List<string> { "n.deleted = 0", "n.status = 'active'" };
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

        AppendScopeIn(command, filters, "n.domain", restrictToDomains);
        var order = byUsage
            ? "ORDER BY COALESCE(u.retrieval_count, 0) DESC, COALESCE(u.last_accessed_utc, n.updated_utc) DESC"
            : "ORDER BY n.updated_utc DESC";
        command.CommandText =
            "SELECT n.id, n.title, n.type, n.domain, n.status, n.payload_json, n.tags_json, n.dedup_key, n.updated_utc " +
            "FROM notes n LEFT JOIN note_usage u ON u.note_id = n.id " +
            $"WHERE {string.Join(" AND ", filters)} {order} LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), null,
                reader.GetString(2), reader.GetString(3), 0.0, reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
        }

        return results;
    }

    /// <summary>
    /// Returns notes related to <paramref name="id"/> by shared tags (most overlap first), scope-restricted.
    /// A linking/dedup suggestion — it never creates links. Empty if the note has no tags.
    /// </summary>
    /// <param name="id">The source note id.</param>
    /// <param name="limit">Max candidates (clamped to [1, 100]).</param>
    /// <param name="restrictToDomains">Auth scope; null = unrestricted.</param>
    public IReadOnlyList<RelatedNote> Related(string id, int limit, IReadOnlyCollection<string>? restrictToDomains)
    {
        var source = Get(id);
        var tags = ParseTags(source?.TagsJson);
        if (tags.Count == 0)
        {
            return Array.Empty<RelatedNote>();
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var filters = new List<string> { "n.deleted = 0", "n.status = 'active'", "n.id <> $id" };
        command.Parameters.AddWithValue("$id", id);
        filters.Add(InClause(command, "je.value", "$tag", tags));
        AppendScopeIn(command, filters, "n.domain", restrictToDomains);

        command.CommandText =
            "SELECT n.id, n.title, n.type, n.domain, group_concat(je.value) AS shared " +
            "FROM notes n, json_each(n.tags_json) je " +
            $"WHERE {string.Join(" AND ", filters)} " +
            "GROUP BY n.id, n.title, n.type, n.domain ORDER BY count(*) DESC, n.updated_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var related = new List<RelatedNote>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var shared = reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            related.Add(new RelatedNote(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), shared));
        }

        return related;
    }

    private static List<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    // Builds "<column> IN ($p0,$p1,...)" binding each value; assumes a non-empty list.
    private static string InClause(SqliteCommand command, string column, string prefix, IReadOnlyList<string> values)
    {
        var placeholders = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            var parameter = $"{prefix}{i}";
            placeholders.Add(parameter);
            command.Parameters.AddWithValue(parameter, values[i]);
        }

        return $"{column} IN ({string.Join(", ", placeholders)})";
    }

    // Appends an optional domain-scope restriction (null = none, empty = match nothing).
    private static void AppendScopeIn(SqliteCommand command, List<string> filters, string column, IReadOnlyCollection<string>? restrict)
    {
        if (restrict is null)
        {
            return;
        }

        if (restrict.Count == 0)
        {
            filters.Add("0");
            return;
        }

        filters.Add(InClause(command, column, "$rd", restrict.ToList()));
    }

    /// <summary>Returns the note's links in both directions (each resolved to the note at the other end).</summary>
    /// <param name="id">The note id.</param>
    public IReadOnlyList<LinkView> Links(string id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 'out' AS dir, l.rel, l.to_id AS other, n.title, n.type, n.domain FROM note_links l JOIN notes n ON n.id = l.to_id " +
            "WHERE l.from_id = $id AND n.deleted = 0 " +
            "UNION ALL " +
            "SELECT 'in' AS dir, l.rel, l.from_id AS other, n.title, n.type, n.domain FROM note_links l JOIN notes n ON n.id = l.from_id " +
            "WHERE l.to_id = $id AND n.deleted = 0 " +
            "ORDER BY dir, rel;";
        command.Parameters.AddWithValue("$id", id);

        var links = new List<LinkView>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new LinkView(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return links;
    }

    /// <summary>Returns a note's history (newest first) from the append-only event log.</summary>
    /// <param name="id">The note id.</param>
    /// <param name="limit">Maximum number of events (clamped to [1, 500]).</param>
    public IReadOnlyList<NoteEvent> Events(string id, int limit = 50)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, op, actor, ts, diff_json FROM note_events WHERE note_id = $id ORDER BY ts DESC, event_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var events = new List<NoteEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var diff = reader.IsDBNull(4) ? null : reader.GetString(4);
            events.Add(new NoteEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                NoteAudit.ChangedFields(diff),
                diff?.Length ?? 0));
        }

        return events;
    }

    /// <summary>
    /// Returns the detail of one history event, or <c>null</c>. <paramref name="fields"/> projects the diff
    /// (full/before/after/changed) and <paramref name="maxChars"/> caps it, so a huge diff need not be read whole.
    /// </summary>
    /// <param name="noteId">The note the event belongs to.</param>
    /// <param name="eventId">The event id (from <see cref="Events"/>).</param>
    /// <param name="maxChars">When set (>=0), truncates the diff to this many characters (sets Truncated).</param>
    /// <param name="fields">Diff projection: full (default) / before / after / changed.</param>
    public NoteEventDetail? Event(string noteId, string eventId, int? maxChars = null, string? fields = null)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, op, actor, ts, diff_json FROM note_events WHERE note_id = $n AND event_id = $e LIMIT 1;";
        command.Parameters.AddWithValue("$n", noteId);
        command.Parameters.AddWithValue("$e", eventId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var rawDiff = reader.IsDBNull(4) ? null : reader.GetString(4);
        var diff = NoteAudit.ProjectDiff(rawDiff, fields);
        var diffChars = diff.Length;
        var truncated = maxChars is int cap && cap >= 0 && diffChars > cap;
        if (truncated)
        {
            diff = diff[..maxChars!.Value];
        }

        return new NoteEventDetail(
            reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), diff, diffChars, truncated);
    }

    /// <summary>Lists full notes for a domain/type (active, non-deleted), newest first.</summary>
    /// <param name="domain">Domain filter.</param>
    /// <param name="type">Type filter.</param>
    /// <param name="limit">Maximum number of notes.</param>
    public IReadOnlyList<Note> List(string domain, string type, int limit = 1000)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {NoteRowMapper.Columns} FROM notes " +
            "WHERE domain = $d AND type = $t AND deleted = 0 AND status = 'active' " +
            "ORDER BY updated_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$d", Identifiers.Normalize(domain));
        command.Parameters.AddWithValue("$t", Identifiers.Normalize(type));
        command.Parameters.AddWithValue("$limit", limit);

        var notes = new List<Note>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notes.Add(NoteRowMapper.Map(reader));
        }

        return notes;
    }

    /// <summary>
    /// Paginated search by optional full-text <paramref name="query"/>, structured filters, and an
    /// optional <paramref name="filter"/> DSL. Returns a bounded page plus <c>total</c>/<c>hasMore</c>
    /// so callers paginate instead of pulling everything. Text queries include a snippet; the full
    /// body is never returned. <paramref name="limit"/> is clamped to [1, <see cref="MaxLimit"/>].
    /// </summary>
    /// <param name="query">Full-text query; when null/blank, results are structured-only (newest first).</param>
    /// <param name="domain">Optional domain filter.</param>
    /// <param name="type">Optional type filter.</param>
    /// <param name="tags">Optional tags; every supplied tag must be present.</param>
    /// <param name="status">Envelope status filter; defaults to <c>active</c>.</param>
    /// <param name="limit">Page size, clamped to [1, 100].</param>
    /// <param name="offset">Number of matches to skip (for paging).</param>
    /// <param name="restrictToDomains">When non-null, restricts results to these domains (auth scope); empty yields nothing.</param>
    /// <param name="filter">Optional filter DSL, e.g. <c>payload.sprint == 'S1' AND status == 'ready'</c>.</param>
    /// <param name="includePayload">When true, each hit also carries its envelope status and payload JSON (still no body), so callers can render a board without a follow-up get per row.</param>
    /// <param name="includeLinks">When true, each hit also carries its links (both directions), so callers can render a graph without a notes_links call per row.</param>
    /// <param name="sort">Optional order-by spec (e.g. <c>payload.spice_level desc</c>); overrides relevance/recency. See <see cref="Query.SortOrder"/>.</param>
    public SearchPage Search(
        string? query = null, string? domain = null, string? type = null,
        IReadOnlyCollection<string>? tags = null, string status = "active",
        int limit = DefaultLimit, int offset = 0, IReadOnlyCollection<string>? restrictToDomains = null,
        string? filter = null, bool includePayload = false, bool includeLinks = false, string? sort = null)
    {
        domain = Identifiers.NormalizeOptional(domain);
        type = Identifiers.NormalizeOptional(type);
        tags = tags?.Select(Identifiers.Normalize).ToList();
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);
        var tokens = string.IsNullOrWhiteSpace(query) ? new List<string>() : SnippetBuilder.Tokenize(query!);
        var useFts = tokens.Count > 0;
        var compiledFilter = string.IsNullOrWhiteSpace(filter) ? null : NoteFilter.Compile(filter!);
        var sortBody = SortOrder.Compile(sort);

        using var connection = _connectionFactory.Create();
        var total = Count(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter);
        var items = Page(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, limit, offset, includePayload, sortBody);
        if (includeLinks)
        {
            items = items.Select(item => item with { Links = Links(item.Id) }).ToList();
        }

        return new SearchPage(items, total, offset, limit, offset + items.Count < total);
    }

    private static int Count(
        SqliteConnection connection, bool useFts, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter)
    {
        using var command = connection.CreateCommand();
        var where = ApplyFilters(command, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter);
        command.CommandText = useFts
            ? $"SELECT count(*) FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid WHERE {where};"
            : $"SELECT count(*) FROM notes n WHERE {where};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IReadOnlyList<SearchResult> Page(
        SqliteConnection connection, bool useFts, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter, int limit, int offset, bool includePayload, string? sortBody = null)
    {
        using var command = connection.CreateCommand();
        var where = ApplyFilters(command, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        // envelope extras are always selected (cheap); they reach the caller only when includePayload is set.
        const string columns = "n.id, n.title, n.type, n.domain, n.body, {0} AS score, n.status, n.payload_json, n.tags_json, n.dedup_key, n.updated_utc, n.project";
        var scoreExpr = useFts ? "bm25(notes_fts)" : "0.0";
        var from = useFts ? "FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid" : "FROM notes n";
        var orderBy = sortBody ?? (useFts ? "score" : "n.updated_utc DESC"); // explicit sort overrides relevance/recency
        command.CommandText =
            $"SELECT {string.Format(System.Globalization.CultureInfo.InvariantCulture, columns, scoreExpr)} " +
            $"{from} WHERE {where} ORDER BY {orderBy} LIMIT $limit OFFSET $offset;";

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            var body = reader.IsDBNull(4) ? null : reader.GetString(4);
            results.Add(new SearchResult(
                reader.GetString(0),
                title,
                useFts ? SnippetBuilder.Build(title, body, tokens) : null,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(5),
                includePayload ? reader.GetString(6) : null,
                includePayload && !reader.IsDBNull(7) ? reader.GetString(7) : null,
                includePayload && !reader.IsDBNull(8) ? reader.GetString(8) : null,
                includePayload && !reader.IsDBNull(9) ? reader.GetString(9) : null,
                includePayload ? reader.GetString(10) : null,
                reader.IsDBNull(11) ? null : reader.GetString(11))); // project (envelope) always returned
        }

        return results;
    }

    // Binds all filter parameters to the command and returns the shared WHERE clause (reused by Count + Page).
    private static string ApplyFilters(
        SqliteCommand command, bool useFts, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter)
    {
        var filters = new List<string>();

        if (useFts)
        {
            // Quote each token as an FTS5 phrase (so arbitrary input can't break MATCH syntax) and make it
            // a prefix match, so longer word forms are found (e.g. "plumb" matches "plumbing"; in Russian a
            // short stem matches its inflected forms).
            filters.Add("notes_fts MATCH $q");
            command.Parameters.AddWithValue("$q", string.Join(' ', tokens.Select(token => $"\"{token}\"*")));
        }

        filters.Add("n.deleted = 0");
        filters.Add("n.status = $status");
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

        AppendTagFilters(command, filters, tags);
        AppendDomainRestriction(command, filters, restrictToDomains);
        AppendCompiledFilter(command, filters, compiledFilter);

        return string.Join(" AND ", filters);
    }

    // Each supplied tag must be present in the note's JSON tag array.
    private static void AppendTagFilters(SqliteCommand command, List<string> filters, IReadOnlyCollection<string>? tags)
    {
        if (tags is null)
        {
            return;
        }

        var index = 0;
        foreach (var tag in tags)
        {
            var parameter = $"$tag{index++}";
            filters.Add($"EXISTS (SELECT 1 FROM json_each(n.tags_json) WHERE json_each.value = {parameter})");
            command.Parameters.AddWithValue(parameter, tag);
        }
    }

    // Limits results to the caller's allowed domains; an empty set means the scope permits nothing.
    private static void AppendDomainRestriction(SqliteCommand command, List<string> filters, IReadOnlyCollection<string>? restrictToDomains)
    {
        if (restrictToDomains is null)
        {
            return;
        }

        if (restrictToDomains.Count == 0)
        {
            filters.Add("0"); // scope allows nothing -> no results
            return;
        }

        var placeholders = new List<string>();
        var index = 0;
        foreach (var allowed in restrictToDomains)
        {
            var parameter = $"$rd{index++}";
            placeholders.Add(parameter);
            command.Parameters.AddWithValue(parameter, allowed);
        }

        filters.Add($"n.domain IN ({string.Join(", ", placeholders)})");
    }

    // Appends the optional user filter DSL fragment and binds its parameters.
    private static void AppendCompiledFilter(SqliteCommand command, List<string> filters, CompiledFilter? compiledFilter)
    {
        if (compiledFilter is null)
        {
            return;
        }

        filters.Add($"({compiledFilter.Sql})"); // group so a DSL clause with OR binds correctly when AND-joined
        foreach (var parameter in compiledFilter.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }
}
