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
        command.Parameters.AddWithValue("$d", domain);
        command.Parameters.AddWithValue("$t", type);
        command.Parameters.AddWithValue("$k", dedupKey);

        using var reader = command.ExecuteReader();
        return reader.Read() ? NoteRowMapper.Map(reader) : null;
    }

    /// <summary>Returns the note's links in both directions (each resolved to the note at the other end).</summary>
    /// <param name="id">The note id.</param>
    public IReadOnlyList<LinkView> Links(string id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 'out' AS dir, l.rel, l.to_id AS other, n.title, n.type FROM note_links l JOIN notes n ON n.id = l.to_id " +
            "WHERE l.from_id = $id AND n.deleted = 0 " +
            "UNION ALL " +
            "SELECT 'in' AS dir, l.rel, l.from_id AS other, n.title, n.type FROM note_links l JOIN notes n ON n.id = l.from_id " +
            "WHERE l.to_id = $id AND n.deleted = 0 " +
            "ORDER BY dir, rel;";
        command.Parameters.AddWithValue("$id", id);

        var links = new List<LinkView>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new LinkView(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
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
            "SELECT op, actor, ts, diff_json FROM note_events WHERE note_id = $id ORDER BY ts DESC, event_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var events = new List<NoteEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new NoteEvent(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return events;
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
        command.Parameters.AddWithValue("$d", domain);
        command.Parameters.AddWithValue("$t", type);
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
    public SearchPage Search(
        string? query = null, string? domain = null, string? type = null,
        IReadOnlyCollection<string>? tags = null, string status = "active",
        int limit = DefaultLimit, int offset = 0, IReadOnlyCollection<string>? restrictToDomains = null,
        string? filter = null, bool includePayload = false)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);
        var tokens = string.IsNullOrWhiteSpace(query) ? new List<string>() : SnippetBuilder.Tokenize(query!);
        var useFts = tokens.Count > 0;
        var compiledFilter = string.IsNullOrWhiteSpace(filter) ? null : NoteFilter.Compile(filter!);

        using var connection = _connectionFactory.Create();
        var total = Count(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter);
        var items = Page(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, limit, offset, includePayload);
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
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter, int limit, int offset, bool includePayload)
    {
        using var command = connection.CreateCommand();
        var where = ApplyFilters(command, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        // status + payload_json are always selected (cheap); they reach the caller only when includePayload is set.
        const string columns = "n.id, n.title, n.type, n.domain, n.body, {0} AS score, n.status, n.payload_json";
        command.CommandText = useFts
            ? $"SELECT {string.Format(System.Globalization.CultureInfo.InvariantCulture, columns, "bm25(notes_fts)")} " +
              $"FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid WHERE {where} " +
              "ORDER BY score LIMIT $limit OFFSET $offset;"
            : $"SELECT {string.Format(System.Globalization.CultureInfo.InvariantCulture, columns, "0.0")} " +
              $"FROM notes n WHERE {where} ORDER BY n.updated_utc DESC LIMIT $limit OFFSET $offset;";

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
                includePayload && !reader.IsDBNull(7) ? reader.GetString(7) : null));
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
            // Quote each token as an FTS5 phrase so arbitrary input can't break MATCH syntax.
            filters.Add("notes_fts MATCH $q");
            command.Parameters.AddWithValue("$q", string.Join(' ', tokens.Select(token => $"\"{token}\"")));
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

        filters.Add(compiledFilter.Sql);
        foreach (var parameter in compiledFilter.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }
}
