using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Maintenance;

/// <summary>
/// Read-only data-quality scan over live notes: flags notes that will be hard to find or maintain
/// (no tags, no dedup key, no title) and links that dangle. Suggests fixes; changes nothing.
/// </summary>
public sealed class NotesLinter
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    /// <summary>Creates the linter over the given database.</summary>
    public NotesLinter(ISqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <summary>
    /// Runs all rules and returns up to <paramref name="limit"/> findings. Optionally focuses one
    /// <paramref name="domain"/>; <paramref name="restrictToDomains"/> (from the caller's scope) bounds
    /// what is visible (null = unrestricted, empty = nothing).
    /// </summary>
    public IReadOnlyList<LintFinding> Lint(string? domain, IReadOnlyCollection<string>? restrictToDomains, int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var connection = _connectionFactory.Create();

        var findings = new List<LintFinding>();
        findings.AddRange(NoteRule(connection, domain, restrictToDomains,
            "(tags_json IS NULL OR json_array_length(tags_json) = 0) AND type <> 'journal'",
            "no_tags", "warn", "Note has no tags — hard to find by facet."));
        findings.AddRange(NoteRule(connection, domain, restrictToDomains,
            "dedup_key IS NULL AND type <> 'journal'",
            "no_dedup_key", "warn", "Note has no dedupKey — not idempotently editable."));
        findings.AddRange(NoteRule(connection, domain, restrictToDomains,
            "(title IS NULL OR trim(title) = '') AND type <> 'journal'",
            "no_title", "info", "Note has no title."));
        findings.AddRange(NoteRule(connection, domain, restrictToDomains,
            "title IS NOT NULL AND trim(title) <> '' AND EXISTS (SELECT 1 FROM notes dup " +
            "WHERE dup.deleted = 0 AND dup.status = 'active' AND dup.id <> notes.id " +
            "AND dup.domain = notes.domain AND dup.type = notes.type AND lower(dup.title) = lower(notes.title))",
            "duplicate", "warn", "Another active note shares this (domain, type, title)."));
        findings.AddRange(BrokenLinks(connection, domain, restrictToDomains));

        return findings.Count <= limit ? findings : findings.GetRange(0, limit);
    }

    private static List<LintFinding> NoteRule(
        SqliteConnection connection, string? domain, IReadOnlyCollection<string>? restrict,
        string predicate, string rule, string severity, string message)
    {
        using var command = connection.CreateCommand();
        var filters = new List<string> { "deleted = 0", "status = 'active'", $"({predicate})" };
        BindScope(command, filters, "domain", domain, restrict);
        command.CommandText = $"SELECT id, title, domain, type FROM notes WHERE {string.Join(" AND ", filters)} ORDER BY domain, type LIMIT 500;";

        var findings = new List<LintFinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            findings.Add(new LintFinding(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), rule, severity, message));
        }

        return findings;
    }

    private static List<LintFinding> BrokenLinks(SqliteConnection connection, string? domain, IReadOnlyCollection<string>? restrict)
    {
        using var command = connection.CreateCommand();
        var filters = new List<string> { "nf.deleted = 0", "(nt.id IS NULL OR nt.deleted = 1)" };
        BindScope(command, filters, "nf.domain", domain, restrict);
        command.CommandText =
            "SELECT l.from_id, nf.title, nf.domain, nf.type, l.to_id, l.rel " +
            "FROM note_links l JOIN notes nf ON nf.id = l.from_id LEFT JOIN notes nt ON nt.id = l.to_id " +
            $"WHERE {string.Join(" AND ", filters)} LIMIT 500;";

        var findings = new List<LintFinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var message = $"Link '{reader.GetString(5)}' points to a missing/deleted note ({reader.GetString(4)}).";
            findings.Add(new LintFinding(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), "broken_link", "warn", message));
        }

        return findings;
    }

    // Appends the optional domain focus + scope restriction to a rule's WHERE clause.
    private static void BindScope(SqliteCommand command, List<string> filters, string domainColumn, string? domain, IReadOnlyCollection<string>? restrict)
    {
        if (domain is not null)
        {
            filters.Add($"{domainColumn} = $domain");
            command.Parameters.AddWithValue("$domain", Identifiers.Normalize(domain));
        }

        if (restrict is null)
        {
            return;
        }

        if (restrict.Count == 0)
        {
            filters.Add("0");
            return;
        }

        var placeholders = new List<string>();
        var index = 0;
        foreach (var allowed in restrict)
        {
            var parameter = $"$rd{index++}";
            placeholders.Add(parameter);
            command.Parameters.AddWithValue(parameter, allowed);
        }

        filters.Add($"{domainColumn} IN ({string.Join(", ", placeholders)})");
    }
}
