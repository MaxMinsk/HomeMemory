using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Query;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

public sealed partial class NotesReader
{
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
    /// <param name="rank">Relevance mode for text queries: <c>hybrid</c> (default — RRF blend of relevance + recency + link-degree + importance + type weight, MEMP-174/193) or <c>lexical</c> (pure BM25). Ignored when an explicit <paramref name="sort"/> is given or the query is structured-only.</param>
    /// <param name="explain">When true (hybrid only), each hit carries its <see cref="ScoreBreakdown"/> (MEMP-177).</param>
    /// <param name="match">Token combine mode for a text query: <c>all</c> (AND), <c>any</c> (OR, ranked), or <c>auto</c> (default — AND, falling back to any-term when AND finds nothing, MEMP-190).</param>
    /// <param name="boostProject">When set (hybrid only), notes in this envelope project are lifted via a soft RRF signal so they edge out equally-relevant notes from other projects, without hiding cross-project hits (MEMP-209).</param>
    /// <param name="projectEquals">When set, a hard filter restricting results to this envelope project (the <c>projectOnly</c> recall mode); applies to every rank/sort.</param>
    public SearchPage Search(
        string? query = null, string? domain = null, string? type = null,
        IReadOnlyCollection<string>? tags = null, string status = "active",
        int limit = DefaultLimit, int offset = 0, IReadOnlyCollection<string>? restrictToDomains = null,
        string? filter = null, bool includePayload = false, bool includeLinks = false, string? sort = null,
        string? rank = null, bool explain = false, string? match = null,
        string? boostProject = null, string? projectEquals = null)
    {
        domain = Identifiers.NormalizeOptional(domain);
        type = Identifiers.NormalizeOptional(type);
        boostProject = Identifiers.NormalizeOptional(boostProject);
        projectEquals = Identifiers.NormalizeOptional(projectEquals);
        tags = tags?.Select(Identifiers.Normalize).ToList();
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);
        var phrase = !string.IsNullOrWhiteSpace(query) && IsQuotedPhrase(query!); // a fully-quoted query is an exact phrase (MEMP-166)
        // Split off '-term' exclusions (MEMP-169); a quoted phrase is taken verbatim (no exclusion parsing).
        var (positives, negatives) = phrase ? (query!, new List<string>()) : SplitQuery(query);
        IReadOnlyList<string> tokens = string.IsNullOrWhiteSpace(positives) ? new List<string>() : SnippetBuilder.Tokenize(positives);
        // Drop RU/EN stop words so a natural question reduces to content tokens (MEMP-191); never strip a phrase.
        if (!phrase && tokens.Count > 0)
        {
            tokens = QueryNormalizer.StripStopWords(tokens);
        }

        var negTokens = negatives.SelectMany(SnippetBuilder.Tokenize).Distinct(StringComparer.Ordinal).ToList();
        var useFts = tokens.Count > 0;
        var compiledFilter = string.IsNullOrWhiteSpace(filter) ? null : NoteFilter.Compile(filter!);
        var sortBody = SortOrder.Compile(sort);
        // With no explicit sort, a note whose dedup_key IS the query ranks first (MEMP-159): searching a key
        // (e.g. "HPA-008") should surface that note above ones that merely mention it.
        var exactKey = sortBody is null && !string.IsNullOrWhiteSpace(positives) ? positives.Trim() : null;
        // Hybrid relevance is the default for a text query with no explicit sort (MEMP-193); rank=lexical opts out.
        var hybrid = useFts && sortBody is null && !string.Equals(rank, "lexical", StringComparison.OrdinalIgnoreCase);
        // Match mode (MEMP-190): an explicit OR/| operator forces any-term; otherwise all|any|auto (default auto).
        var mode = (match ?? "auto").ToLowerInvariant();
        var any = string.Equals(mode, "any", StringComparison.Ordinal) || (!phrase && QueryNormalizer.HasOrOperator(positives));

        using var connection = _connectionFactory.Create();

        // MEMP-225: a query that IS a ticket key (e.g. "TRD-131") is an exact lookup, not free text.
        if (TryExactKeyLookup(connection, exactKey, phrase, any, filter, tags, offset, domain, type, status, restrictToDomains, limit, includePayload, includeLinks) is { } exact)
        {
            return exact;
        }

        var total = Count(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, phrase, negTokens, any, projectEquals);
        // auto: an AND query that matched nothing is re-run as any-term ranked partials, flagged relaxed (MEMP-190).
        var relaxed = false;
        if (useFts && !any && total == 0 && string.Equals(mode, "auto", StringComparison.Ordinal) && tokens.Count > 1)
        {
            any = true;
            relaxed = true;
            total = Count(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, phrase, negTokens, any, projectEquals);
        }

        var items = hybrid
            ? HybridPage(connection, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, limit, offset, includePayload, exactKey, phrase, negTokens, explain, any, boostProject, projectEquals)
            : Page(connection, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, limit, offset, includePayload, sortBody, exactKey, phrase, negTokens, any, projectEquals);
        if (includeLinks)
        {
            items = items.Select(item => item with { Links = Links(item.Id) }).ToList();
        }

        return new SearchPage(items, total, offset, limit, offset + items.Count < total, relaxed);
    }

    private static int Count(
        SqliteConnection connection, bool useFts, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter, bool phrase = false, IReadOnlyList<string>? negTokens = null, bool matchAny = false, string? projectEquals = null)
    {
        using var command = connection.CreateCommand();
        var where = ApplyFilters(command, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, phrase, negTokens, matchAny, projectEquals);
        command.CommandText = useFts
            ? $"SELECT count(*) FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid WHERE {where};"
            : $"SELECT count(*) FROM notes n WHERE {where};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private IReadOnlyList<SearchResult> Page(
        SqliteConnection connection, bool useFts, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter, int limit, int offset, bool includePayload, string? sortBody = null, string? exactKey = null, bool phrase = false, IReadOnlyList<string>? negTokens = null, bool matchAny = false, string? projectEquals = null)
    {
        using var command = connection.CreateCommand();
        var where = ApplyFilters(command, useFts, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, phrase, negTokens, matchAny, projectEquals);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        // envelope extras are always selected (cheap); they reach the caller only when includePayload is set.
        const string columns = "n.id, n.title, n.type, n.domain, n.body, {0} AS score, n.status, n.payload_json, n.tags_json, n.dedup_key, n.updated_utc, n.project";
        var scoreExpr = useFts ? Bm25Weights.Expression : "0.0";
        var from = useFts ? "FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid" : "FROM notes n";
        var orderBy = sortBody ?? (useFts ? "score" : "n.updated_utc DESC"); // explicit sort overrides relevance/recency
        if (exactKey is not null)
        {
            // Rank an exact dedup_key first (MEMP-159), then an exact title (MEMP-160), then relevance/recency.
            // mem_lower, not lower(): a title is free-form text and SQLite's fold is ASCII-only, so a Russian
            // title typed in another case missed its own exact-match tier (MEMP-238).
            command.Parameters.AddWithValue("$exactkey", exactKey);
            orderBy = "(CASE WHEN n.dedup_key IS NOT NULL AND mem_lower(n.dedup_key) = mem_lower($exactkey) THEN 0 " +
                "WHEN n.title IS NOT NULL AND mem_lower(trim(n.title)) = mem_lower($exactkey) THEN 1 ELSE 2 END), " + orderBy;
        }
        command.CommandText =
            $"SELECT {string.Format(System.Globalization.CultureInfo.InvariantCulture, columns, scoreExpr)} " +
            $"{from} WHERE {where} ORDER BY {orderBy} LIMIT $limit OFFSET $offset;";

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            var body = reader.IsDBNull(4) ? null : reader.GetString(4);
            var rowType = reader.GetString(2);
            var payloadJson = reader.IsDBNull(7) ? null : reader.GetString(7);
            var updatedUtc = reader.GetString(10);
            results.Add(new SearchResult(
                reader.GetString(0),
                title,
                useFts ? SnippetBuilder.Build(title, body, tokens) : null,
                rowType,
                reader.GetString(3),
                reader.GetDouble(5),
                includePayload ? reader.GetString(6) : null,
                includePayload ? payloadJson : null,
                includePayload && !reader.IsDBNull(8) ? reader.GetString(8) : null,
                includePayload && !reader.IsDBNull(9) ? reader.GetString(9) : null,
                includePayload ? updatedUtc : null,
                reader.IsDBNull(11) ? null : reader.GetString(11), // project (envelope) always returned
                Staleness: StalenessOf(rowType, payloadJson, updatedUtc)));
        }

        return results;
    }

    // Hybrid relevance (MEMP-174): pull a bounded BM25 candidate pool (with link-degree + payload), re-rank it by
    // RRF over relevance/recency/link-degree/importance in C#, then page within the re-ranked pool. exact-key matches
    // still float to the top. Deep paging past the pool returns nothing (recall uses small limits) — the pool caps cost.
    private IReadOnlyList<SearchResult> HybridPage(
        SqliteConnection connection, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter, int limit, int offset,
        bool includePayload, string? exactKey, bool phrase, IReadOnlyList<string> negTokens, bool explain, bool matchAny = false,
        string? boostProject = null, string? projectEquals = null)
    {
        using var command = connection.CreateCommand();
        var where = ApplyFilters(command, true, tokens, domain, type, tags, status, restrictToDomains, compiledFilter, phrase, negTokens, matchAny, projectEquals);
        command.Parameters.AddWithValue("$pool", RankingWeights.PoolSize);
        command.CommandText =
            $"SELECT n.id, n.title, n.type, n.domain, n.body, {Bm25Weights.Expression} AS score, n.status, n.payload_json, " +
            "n.tags_json, n.dedup_key, n.updated_utc, n.project, " +
            "(SELECT count(*) FROM note_links l WHERE l.from_id = n.id OR l.to_id = n.id) AS degree " +
            $"FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid WHERE {where} " +
            $"ORDER BY {Bm25Weights.Expression} LIMIT $pool;";

        var rows = new List<RankRow>();
        var terms = TitleRelevance.Terms(tokens); // stemmed once for the whole pool, not per row (MEMP-237)
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var title = reader.IsDBNull(1) ? null : reader.GetString(1);
                var body = reader.IsDBNull(4) ? null : reader.GetString(4);
                var bm25 = reader.GetDouble(5);
                var payloadJson = reader.IsDBNull(7) ? null : reader.GetString(7);
                var tagsJson = reader.IsDBNull(8) ? null : reader.GetString(8);
                var dedupKey = reader.IsDBNull(9) ? null : reader.GetString(9);
                var updatedUtc = reader.GetString(10);
                var degree = reader.GetInt64(12);
                var rowType = reader.GetString(2);
                var result = new SearchResult(
                    reader.GetString(0), title, SnippetBuilder.Build(title, body, tokens),
                    rowType, reader.GetString(3), bm25,
                    includePayload ? reader.GetString(6) : null,
                    includePayload ? payloadJson : null,
                    includePayload ? tagsJson : null,
                    includePayload ? dedupKey : null,
                    includePayload ? updatedUtc : null,
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    Staleness: StalenessOf(rowType, payloadJson, updatedUtc));
                var project = reader.IsDBNull(11) ? null : reader.GetString(11);
                rows.Add(new RankRow(
                    result, ExactKeyTier(dedupKey, title, exactKey), -bm25, TitleRelevance.Goodness(title, terms),
                    HybridRanker.RecencyGoodness(updatedUtc), degree, HybridRanker.ImportanceGoodness(payloadJson, tagsJson),
                    HybridRanker.TypeGoodness(reader.GetString(2)), HybridRanker.ProjectGoodness(project, boostProject)));
            }
        }

        // Up-weight the project signal only when a project was requested; otherwise it's a no-op tie (see ProjectGoodness).
        var weights = boostProject is null ? RankingWeights.Default : RankingWeights.ProjectBoosted;
        var fused = HybridRanker.Fuse(rows, weights);
        return fused.Skip(offset).Take(limit)
            .Select(item => explain ? item.Result with { Explain = item.Breakdown } : item.Result)
            .ToList();
    }

    // Exact-key tier used to keep a searched key/title on top of any ranking (MEMP-159/160).
    private static int ExactKeyTier(string? dedupKey, string? title, string? exactKey)
    {
        if (exactKey is null)
        {
            return 2;
        }

        if (dedupKey is not null && string.Equals(dedupKey, exactKey, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return title is not null && string.Equals(title.Trim(), exactKey, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    // Splits a query into its positive text and the '-term' exclusions (MEMP-169). A lone '-' is kept as text.
    private static (string Positives, List<string> Negatives) SplitQuery(string? query)
    {
        var positives = new List<string>();
        var negatives = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var chunk in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (chunk.Length > 1 && chunk[0] == '-')
                {
                    negatives.Add(chunk[1..]);
                }
                else
                {
                    positives.Add(chunk);
                }
            }
        }

        return (string.Join(' ', positives), negatives);
    }

    // True when the whole query is a single double-quoted segment (an exact-phrase request).
    private static bool IsQuotedPhrase(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var q = query.Trim();
        return q.Length >= 3 && q[0] == '"' && q[^1] == '"' && q.IndexOf('"', 1) == q.Length - 1;
    }

    // A query shaped like a ticket key (PREFIX-digits, e.g. TRD-131 / MEMP-215): treated as an exact lookup (MEMP-225).
    private static readonly System.Text.RegularExpressions.Regex TicketKeyPattern =
        new("^[A-Za-z][A-Za-z0-9]*-[0-9]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private static bool LooksLikeTicketKey(string query) => TicketKeyPattern.IsMatch(query.Trim());

    // MEMP-225: when the whole query is a ticket key and no other constraints conflict, resolve it as an exact
    // dedup_key lookup; null lets normal ranked search proceed. Hyphenated keys otherwise tokenize to TRD OR 131.
    private SearchPage? TryExactKeyLookup(
        SqliteConnection connection, string? exactKey, bool phrase, bool any, string? filter,
        IReadOnlyCollection<string>? tags, int offset, string? domain, string? type, string status,
        IReadOnlyCollection<string>? restrict, int limit, bool includePayload, bool includeLinks)
    {
        var allowed = !phrase && !any && offset == 0 && string.IsNullOrWhiteSpace(filter) && (tags is null || tags.Count == 0);
        if (!allowed || exactKey is null || !LooksLikeTicketKey(exactKey))
        {
            return null;
        }

        return ExactKeyPage(connection, exactKey, domain, type, status, restrict, limit, includePayload, includeLinks);
    }

    // MEMP-225: returns a single-hit page for an exact dedup_key match, or null when zero/ambiguous (fall through
    // to normal search). Case-insensitive on the key. Honors domain/type/status/scope and includePayload/Links.
    private SearchPage? ExactKeyPage(
        SqliteConnection connection, string key, string? domain, string? type, string status,
        IReadOnlyCollection<string>? restrict, int limit, bool includePayload, bool includeLinks)
    {
        using var command = connection.CreateCommand();
        // COLLATE NOCASE folds ASCII only, which is all this path needs: it is reached solely for a query that
        // matched the ASCII ticket-key shape (see LooksLikeTicketKey), so there is no non-Latin case to fold.
        var filters = new List<string> { "deleted = 0", "dedup_key = $k COLLATE NOCASE" };
        command.Parameters.AddWithValue("$k", key.Trim());
        if (!string.IsNullOrWhiteSpace(status))
        {
            filters.Add("status = $st");
            command.Parameters.AddWithValue("$st", status);
        }

        if (domain is not null)
        {
            filters.Add("domain = $d");
            command.Parameters.AddWithValue("$d", domain);
        }

        if (type is not null)
        {
            filters.Add("type = $t");
            command.Parameters.AddWithValue("$t", type);
        }

        AppendScopeIn(command, filters, "domain", restrict);
        command.CommandText =
            "SELECT id, title, type, domain, status, payload_json, tags_json, dedup_key, updated_utc, project " +
            $"FROM notes WHERE {string.Join(" AND ", filters)} LIMIT 2;";

        var rows = new List<SearchResult>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var exactType = reader.GetString(2);
                var exactPayload = reader.IsDBNull(5) ? null : reader.GetString(5);
                var exactUpdated = reader.IsDBNull(8) ? null : reader.GetString(8);
                rows.Add(new SearchResult(
                    reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), null,
                    exactType, reader.GetString(3), 0.0,
                    includePayload ? reader.GetString(4) : null,
                    includePayload ? exactPayload : null,
                    includePayload && !reader.IsDBNull(6) ? reader.GetString(6) : null,
                    includePayload && !reader.IsDBNull(7) ? reader.GetString(7) : null,
                    includePayload ? exactUpdated : null,
                    includePayload && !reader.IsDBNull(9) ? reader.GetString(9) : null,
                    Staleness: StalenessOf(exactType, exactPayload, exactUpdated)));
            }
        }

        if (rows.Count != 1)
        {
            return null; // no match, or ambiguous across scope — let normal ranked search handle it
        }

        var hit = includeLinks ? rows[0] with { Links = Links(rows[0].Id) } : rows[0];
        return new SearchPage(new[] { hit }, 1, 0, limit, false);
    }

    // Binds all filter parameters to the command and returns the shared WHERE clause (reused by Count + Page).
    private static string ApplyFilters(
        SqliteCommand command, bool useFts, IReadOnlyList<string> tokens,
        string? domain, string? type, IReadOnlyCollection<string>? tags, string status,
        IReadOnlyCollection<string>? restrictToDomains, CompiledFilter? compiledFilter, bool phrase = false, IReadOnlyList<string>? negTokens = null, bool matchAny = false, string? projectEquals = null)
    {
        var filters = new List<string>();

        if (useFts)
        {
            string match;
            if (phrase)
            {
                // A fully-quoted query is an exact ordered phrase (MEMP-166): no prefix, no stem expansion.
                match = $"\"{string.Join(' ', tokens)}\"";
            }
            else
            {
                // Each token a quoted FTS5 prefix phrase, combined across all columns. Default is AND (space);
                // any-term mode (MEMP-190) joins with OR so partial matches return ranked. Stems part (MEMP-024):
                // the stemmed tokens against the `stems` sidecar column with the same combiner, so word forms match
                // (ANRs/ANR, Russian cases). Raw OR stems, so the raw path always still wins; stems only ADD recall.
                var combiner = matchAny ? " OR " : " ";
                var raw = string.Join(combiner, tokens.Select(token => $"\"{token}\"*"));
                var stemmed = SearchStems.StemQueryTokens(tokens);
                match = stemmed.Count == 0
                    ? raw
                    : $"({raw}) OR (stems : ({string.Join(combiner, stemmed.Select(stem => $"\"{stem}\""))}))";
            }

            if (negTokens is { Count: > 0 })
            {
                // Exclude notes matching any '-term' (MEMP-169): FTS5 `(match) NOT (a* b* ...)`.
                match = $"({match}) NOT ({string.Join(' ', negTokens.Select(token => $"\"{token}\"*"))})";
            }

            filters.Add("notes_fts MATCH $q");
            command.Parameters.AddWithValue("$q", match);
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

        AppendProjectFilter(command, filters, projectEquals);
        AppendTagFilters(command, filters, tags);
        AppendDomainRestriction(command, filters, restrictToDomains);
        AppendCompiledFilter(command, filters, compiledFilter);

        return string.Join(" AND ", filters);
    }

    // Hard project filter (projectOnly recall, MEMP-209): parameterized, so it composes with any rank/sort.
    private static void AppendProjectFilter(SqliteCommand command, List<string> filters, string? projectEquals)
    {
        if (projectEquals is null)
        {
            return;
        }

        filters.Add("n.project = $projeq");
        command.Parameters.AddWithValue("$projeq", projectEquals);
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
