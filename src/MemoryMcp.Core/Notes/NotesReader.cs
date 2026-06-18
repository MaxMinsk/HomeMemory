using System.Text.Json;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Notes;

/// <summary>Read-side note access: get by id, list, and paginated full-text / structured search.</summary>
public sealed partial class NotesReader
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

    /// <summary>Below this many body characters (with no title and an empty payload) a candidate is not worth capturing.</summary>
    public const int CaptureMinBodyChars = 10;

    /// <summary>Largest number of nodes a single <see cref="Graph"/> traversal returns before truncating.</summary>
    public const int MaxGraphNodes = 200;

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
    /// <c>null</c> only when the note does not exist; an empty body yields an empty slice. The window is sliced
    /// in SQLite (<c>substr</c>/<c>length</c>) so a multi-megabyte body is never materialized in memory just to
    /// return a few kilobytes. Offsets and lengths are Unicode code points (SQLite text semantics).
    /// </summary>
    /// <param name="id">The note id.</param>
    /// <param name="offset">Start character offset (clamped to [0, totalChars]).</param>
    /// <param name="limitChars">Max characters to return (clamped to [1, <see cref="MaxReadChars"/>]).</param>
    public NoteReadSlice? ReadBody(string id, int offset = 0, int limitChars = DefaultReadChars)
    {
        using var connection = _connectionFactory.Create();

        int total;
        string updatedUtc;
        using (var head = connection.CreateCommand())
        {
            // length() counts the body without transferring it — so we can clamp the offset without loading it.
            head.CommandText = "SELECT length(body), updated_utc FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
            head.Parameters.AddWithValue("$id", id);
            using var headReader = head.ExecuteReader();
            if (!headReader.Read())
            {
                return null; // note does not exist
            }

            total = headReader.IsDBNull(0) ? 0 : (int)headReader.GetInt64(0); // length(NULL) = NULL -> empty body
            updatedUtc = headReader.GetString(1);
        }

        var start = Math.Clamp(offset, 0, total);
        var take = Math.Clamp(limitChars, 1, MaxReadChars);

        string content;
        int returned;
        using (var window = connection.CreateCommand())
        {
            // substr() is 1-based; length(substr(...)) keeps Returned and Total in the same (code-point) units.
            window.CommandText =
                "SELECT substr(body, $start + 1, $take), length(substr(body, $start + 1, $take)) " +
                "FROM notes WHERE id = $id AND deleted = 0 LIMIT 1;";
            window.Parameters.AddWithValue("$id", id);
            window.Parameters.AddWithValue("$start", start);
            window.Parameters.AddWithValue("$take", take);
            using var windowReader = window.ExecuteReader();
            windowReader.Read();
            content = windowReader.IsDBNull(0) ? string.Empty : windowReader.GetString(0);
            returned = windowReader.IsDBNull(1) ? 0 : (int)windowReader.GetInt64(1);
        }

        var truncated = start + returned < total;
        return new NoteReadSlice(id, content, start, returned, total, truncated, truncated ? start + returned : null, updatedUtc);
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
}
