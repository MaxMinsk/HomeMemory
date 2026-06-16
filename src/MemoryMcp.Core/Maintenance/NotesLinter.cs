using System.Globalization;
using System.Text.RegularExpressions;
using MemoryMcp.Core.Naming;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Maintenance;

/// <summary>
/// Read-only data-quality scan over live notes: flags notes that will be hard to find or maintain
/// (no tags, no dedup key, no title), links that dangle, notes left <c>unstructured</c> past a review
/// window, and bodies/payloads that look like they embed a secret. Suggests fixes; changes nothing.
/// </summary>
public sealed class NotesLinter
{
    // A note tagged 'unstructured' and untouched for longer than this is surfaced as a review-queue item.
    private const int StaleUnstructuredDays = 30;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // Heuristics for embedded credentials. Order is irrelevant; the first match names the finding.
    private static readonly (string Name, Regex Pattern)[] SecretHeuristics =
    {
        ("private_key_block", new Regex("-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, RegexTimeout)),
        ("aws_access_key", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.None, RegexTimeout)),
        ("github_token", new Regex("gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.None, RegexTimeout)),
        ("slack_token", new Regex("xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.None, RegexTimeout)),
        ("jwt", new Regex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{6,}", RegexOptions.None, RegexTimeout)),
        ("credential_assignment", new Regex(
            @"\b(password|passwd|secret|api[_-]?key|access[_-]?token|bearer)\b\s*[:=]\s*[""']?[^\s""']{6,}",
            RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, RegexTimeout)),
    };

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the linter over the given database.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="timeProvider">Clock for the staleness cutoff; defaults to the system clock.</param>
    public NotesLinter(ISqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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
        findings.AddRange(StaleUnstructured(connection, domain, restrictToDomains));
        findings.AddRange(BrokenLinks(connection, domain, restrictToDomains));
        findings.AddRange(PossibleSecrets(connection, domain, restrictToDomains));

        return findings.Count <= limit ? findings : findings.GetRange(0, limit);
    }

    // Notes still tagged 'unstructured' and untouched for over StaleUnstructuredDays — a review queue so
    // autonomous appends don't quietly rot. updated_utc is stored round-trip ("O") UTC, so a same-format
    // string compare is chronological.
    private List<LintFinding> StaleUnstructured(SqliteConnection connection, string? domain, IReadOnlyCollection<string>? restrict)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-StaleUnstructuredDays)
            .ToString("O", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        var filters = new List<string>
        {
            "deleted = 0", "status = 'active'", "tags_json IS NOT NULL",
            "EXISTS (SELECT 1 FROM json_each(notes.tags_json) je WHERE je.value = 'unstructured')",
            "updated_utc < $cutoff",
        };
        command.Parameters.AddWithValue("$cutoff", cutoff);
        BindScope(command, filters, "domain", domain, restrict);
        command.CommandText = $"SELECT id, title, domain, type FROM notes WHERE {string.Join(" AND ", filters)} ORDER BY updated_utc LIMIT 500;";

        var findings = new List<LintFinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            findings.Add(new LintFinding(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), "stale_unstructured", "warn",
                $"Tagged 'unstructured' and untouched for over {StaleUnstructuredDays} days — structure or archive it."));
        }

        return findings;
    }

    // Heuristic scan of body + payload for embedded credentials. Reports WHICH note and WHICH heuristic,
    // never the matched text itself, so the lint output cannot leak the secret it found.
    private static List<LintFinding> PossibleSecrets(SqliteConnection connection, string? domain, IReadOnlyCollection<string>? restrict)
    {
        using var command = connection.CreateCommand();
        var filters = new List<string> { "deleted = 0", "status = 'active'", "(body IS NOT NULL OR payload_json IS NOT NULL)" };
        BindScope(command, filters, "domain", domain, restrict);
        command.CommandText = $"SELECT id, title, domain, type, body, payload_json FROM notes WHERE {string.Join(" AND ", filters)} ORDER BY domain, type LIMIT 500;";

        var findings = new List<LintFinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hit = MatchSecret(reader.IsDBNull(4) ? null : reader.GetString(4))
                ?? MatchSecret(reader.IsDBNull(5) ? null : reader.GetString(5));
            if (hit is null)
            {
                continue;
            }

            findings.Add(new LintFinding(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), "possible_secret", "warn",
                $"Possible secret (heuristic: {hit}) — review and remove/redact; this memory is shared across agents."));
        }

        return findings;
    }

    private static string? MatchSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var (name, pattern) in SecretHeuristics)
        {
            if (pattern.IsMatch(text))
            {
                return name;
            }
        }

        return null;
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
