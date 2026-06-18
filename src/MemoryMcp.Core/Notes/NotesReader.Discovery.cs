using System.Text.Json;
using MemoryMcp.Core.Naming;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

public sealed partial class NotesReader
{
    private const char CursorSeparator = '~'; // not present in ISO timestamps or hex event ids

    /// <summary>
    /// Changefeed (MEMP-155): note changes after the opaque <paramref name="since"/> cursor, oldest-first, from the
    /// append-only event log — for polling-based "subscriptions". The cursor is (ts, event_id); pass back the
    /// returned <c>NextCursor</c> to resume. Scope-restricted; optional domain/type filter. Hard-deleted (purged)
    /// notes' events are skipped (no note row to resolve).
    /// </summary>
    /// <param name="since">Opaque cursor from a prior call (null/empty = from the beginning).</param>
    /// <param name="domain">Optional domain filter.</param>
    /// <param name="type">Optional type filter.</param>
    /// <param name="limit">Page size (clamped to [1, <see cref="MaxLimit"/>]).</param>
    /// <param name="restrictToDomains">Auth scope; null = unrestricted, empty = nothing.</param>
    public NoteChangePage Changes(string? since, string? domain, string? type, int limit, IReadOnlyCollection<string>? restrictToDomains)
    {
        domain = Identifiers.NormalizeOptional(domain);
        type = Identifiers.NormalizeOptional(type);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var (sinceTs, sinceEvent) = SplitCursor(since);

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var filters = new List<string>
        {
            "n.deleted = 0",
            "(e.ts > $ts OR (e.ts = $ts AND e.event_id > $eid))", // (ts, event_id) cursor: stable across same-ts ties
        };
        command.Parameters.AddWithValue("$ts", sinceTs);
        command.Parameters.AddWithValue("$eid", sinceEvent);
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
        command.CommandText =
            "SELECT e.event_id, e.op, e.ts, e.note_id, n.title, n.type, n.domain, n.project, n.status " +
            "FROM note_events e JOIN notes n ON n.id = e.note_id " +
            $"WHERE {string.Join(" AND ", filters)} ORDER BY e.ts, e.event_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit + 1); // fetch one extra to detect hasMore

        var changes = new List<NoteChange>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                changes.Add(new NoteChange(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
            }
        }

        var hasMore = changes.Count > limit;
        if (hasMore)
        {
            changes.RemoveAt(changes.Count - 1); // drop the lookahead row
        }

        var nextCursor = changes.Count == 0 ? since : changes[^1].Ts + CursorSeparator + changes[^1].EventId;
        return new NoteChangePage(changes, nextCursor, hasMore);
    }

    // Splits the opaque "<ts><sep><eventId>" cursor; empty halves => from the beginning.
    private static (string Ts, string Event) SplitCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (string.Empty, string.Empty);
        }

        var parts = cursor.Split(CursorSeparator, 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
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

    /// <summary>
    /// Rule-based capture advice (MEMP-111): given a candidate note, recommends <c>save</c> / <c>update</c> /
    /// <c>skip</c> / <c>ask</c> by checking — in order — for negligible content, an identical note (same content
    /// hash), an existing same-title/type note to update instead, and merely similar notes (FTS) to review. Reads
    /// only; writes nothing. Scoped to <paramref name="domain"/>; <paramref name="restrictToDomains"/> bounds the
    /// similarity search.
    /// </summary>
    /// <param name="domain">The domain the note would be written to.</param>
    /// <param name="type">The candidate note type.</param>
    /// <param name="title">The candidate title.</param>
    /// <param name="body">The candidate body.</param>
    /// <param name="payloadJson">The candidate payload JSON.</param>
    /// <param name="tagsJson">The candidate tags JSON.</param>
    /// <param name="restrictToDomains">Auth scope for the similarity search (null = unrestricted).</param>
    public CaptureSuggestion SuggestCapture(
        string domain, string type, string? title, string? body, string? payloadJson, string? tagsJson,
        IReadOnlyCollection<string>? restrictToDomains)
    {
        domain = Identifiers.Normalize(domain);
        type = Identifiers.Normalize(type);
        var trimmedTitle = title?.Trim();
        var hasPayload = !string.IsNullOrWhiteSpace(payloadJson) && payloadJson.Trim() is not ("{}" or "[]");
        if (string.IsNullOrWhiteSpace(trimmedTitle) && (body?.Trim().Length ?? 0) < CaptureMinBodyChars && !hasPayload)
        {
            return new CaptureSuggestion("skip", "Nothing substantive to capture (no title, negligible body, empty payload).", Array.Empty<CaptureCandidate>());
        }

        var hash = ContentHash.Compute(type, title, body, payloadJson, tagsJson);
        var identical = MatchNotes(domain, "content_hash = $h", "identical content", new[] { ("$h", hash) });
        if (identical.Count > 0)
        {
            return new CaptureSuggestion("skip", "An identical note already exists — no need to capture it again.", identical);
        }

        if (!string.IsNullOrWhiteSpace(trimmedTitle))
        {
            var sameTitle = MatchNotes(domain, "type = $t AND lower(trim(title)) = lower($title)", "same title & type",
                new[] { ("$t", type), ("$title", trimmedTitle!) });
            if (sameTitle.Count > 0)
            {
                return new CaptureSuggestion("update", "A note of the same type and title exists — update it instead of creating a duplicate.", sameTitle);
            }
        }

        var probe = !string.IsNullOrWhiteSpace(trimmedTitle) ? trimmedTitle! : FirstWords(body, 12);
        var similar = SimilarByText(domain, type, probe, restrictToDomains);
        if (similar.Count > 0)
        {
            return new CaptureSuggestion("ask", "Similar notes exist — review them before saving a new one.", similar);
        }

        return new CaptureSuggestion("save", "No similar note found — safe to capture.", Array.Empty<CaptureCandidate>());
    }

    // Notes of the same domain+type sharing ANY significant token with the probe (OR-FTS, ranked) — a soft
    // "similar" signal, unlike Search's all-terms-must-match AND. Empty probe => no candidates.
    private List<CaptureCandidate> SimilarByText(string domain, string type, string probe, IReadOnlyCollection<string>? restrictToDomains)
    {
        var tokens = SnippetBuilder.Tokenize(probe);
        if (tokens.Count == 0)
        {
            return new List<CaptureCandidate>();
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        var filters = new List<string> { "notes_fts MATCH $q", "n.deleted = 0", "n.status = 'active'", "n.domain = $d", "n.type = $t" };
        command.Parameters.AddWithValue("$q", string.Join(" OR ", tokens.Select(token => $"\"{token}\"*")));
        command.Parameters.AddWithValue("$d", domain);
        command.Parameters.AddWithValue("$t", type);
        AppendScopeIn(command, filters, "n.domain", restrictToDomains);
        command.CommandText =
            "SELECT n.id, n.title, n.type, n.domain FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid " +
            $"WHERE {string.Join(" AND ", filters)} ORDER BY bm25(notes_fts) LIMIT 4;";

        var matches = new List<CaptureCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(new CaptureCandidate(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), "text match"));
        }

        return matches;
    }

    // Returns up to 5 active notes in a domain matching a predicate, each tagged with the given reason.
    private List<CaptureCandidate> MatchNotes(string domain, string predicate, string reason, IReadOnlyList<(string Name, string Value)> bindings)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, title, type, domain FROM notes WHERE domain = $d AND deleted = 0 AND status = 'active' AND ({predicate}) LIMIT 5;";
        command.Parameters.AddWithValue("$d", domain);
        foreach (var (name, value) in bindings)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var matches = new List<CaptureCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(new CaptureCandidate(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reason));
        }

        return matches;
    }

    private static string FirstWords(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(count));
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

    /// <summary>
    /// Returns the link-graph neighborhood of a note out to <paramref name="maxHops"/> hops: a breadth-first walk
    /// over <c>note_links</c> in both directions, scope-restricted, returning nodes (with hop distance) and the
    /// edges among them. Returns <c>null</c> only when the root is missing or out of scope. Bounded by
    /// <see cref="MaxGraphNodes"/> (sets <c>Truncated</c>) so a hub note can't return the whole store.
    /// </summary>
    /// <param name="id">The root note id.</param>
    /// <param name="maxHops">Traversal depth (clamped to [1, 5]).</param>
    /// <param name="restrictToDomains">Auth scope; restricts both the root and every neighbor (null = unrestricted).</param>
    public NoteGraph? Graph(string id, int maxHops, IReadOnlyCollection<string>? restrictToDomains)
    {
        var root = Get(id);
        if (root is null || (restrictToDomains is not null && !restrictToDomains.Contains(root.Domain)))
        {
            return null;
        }

        maxHops = Math.Clamp(maxHops, 1, 5);
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal)
        {
            [root.Id] = new GraphNode(root.Id, root.Title, root.Type, root.Domain, 0),
        };
        var edges = new List<GraphEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new List<string> { root.Id };
        var truncated = false;

        for (var hop = 1; hop <= maxHops && frontier.Count > 0 && !truncated; hop++)
        {
            var next = new List<string>();
            foreach (var nodeId in frontier)
            {
                foreach (var link in Links(nodeId))
                {
                    if (restrictToDomains is not null && !restrictToDomains.Contains(link.Domain))
                    {
                        continue; // neighbor outside the caller's scope — omit the node and the edge to it
                    }

                    var (fromId, toId) = link.Direction == "out" ? (nodeId, link.NoteId) : (link.NoteId, nodeId);
                    if (edgeKeys.Add($"{fromId}{toId}{link.Rel}"))
                    {
                        edges.Add(new GraphEdge(fromId, toId, link.Rel));
                    }

                    if (nodes.ContainsKey(link.NoteId))
                    {
                        continue;
                    }

                    if (nodes.Count >= MaxGraphNodes)
                    {
                        truncated = true;
                        break;
                    }

                    nodes[link.NoteId] = new GraphNode(link.NoteId, link.Title, link.Type, link.Domain, hop);
                    next.Add(link.NoteId);
                }

                if (truncated)
                {
                    break;
                }
            }

            frontier = next;
        }

        return new NoteGraph(root.Id, nodes.Values.ToList(), edges, truncated);
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
}
