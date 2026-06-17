using System.Globalization;

namespace MemoryMcp.Core.Query;

/// <summary>
/// Type-aware recency decay (MEMP-037): a note's freshness fades with age, with a HALF-LIFE that depends on its
/// type — ephemeral types (episode, journal) fade in days, durable knowledge (recipe, reference, skill) effectively
/// never. Powers the <c>recency</c> sort, which orders by age normalized to each type's half-life, so a fresh
/// ephemeral note and an old durable note are compared fairly (a week-old journal sinks below a year-old recipe).
/// <see cref="Score"/> exposes the 0..1 decay multiplier for other rankers.
/// </summary>
public static class RecencyDecay
{
    /// <summary>Half-life (days) used for a type with no specific entry.</summary>
    public const double DefaultHalfLifeDays = 90.0;

    // How many days until a note of this type is "half as fresh". Durable/reference types are effectively timeless.
    private static readonly IReadOnlyDictionary<string, double> HalfLives = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["episode"] = 7.0,
        ["journal"] = 14.0,
        ["measurement"] = 30.0,
        ["memory_evolution_suggestion"] = 30.0,
        ["backlog_item"] = 45.0,
        ["project_state"] = 60.0,
        ["idea"] = 120.0,
        ["sprint"] = 120.0,
        ["preference"] = 180.0,
        ["decision"] = 180.0,
        ["fact"] = 180.0,
        ["menu"] = 365.0,
        ["recipe"] = 3650.0,
        ["reference"] = 3650.0,
        ["technique"] = 3650.0,
        ["skill"] = 3650.0,
        ["memory_rule"] = 3650.0,
        ["equipment"] = 3650.0,
        ["seed_variety"] = 3650.0,
        ["spice"] = 3650.0,
    };

    /// <summary>The half-life (days) for a note type (<see cref="DefaultHalfLifeDays"/> when unknown).</summary>
    /// <param name="type">The note type.</param>
    public static double HalfLifeDays(string? type) =>
        type is not null && HalfLives.TryGetValue(type, out var days) ? days : DefaultHalfLifeDays;

    /// <summary>The decay multiplier in (0, 1]: 1 when brand new, 0.5 at one half-life, approaching 0 as it ages.</summary>
    /// <param name="ageDays">Age of the note in days (negative is treated as 0).</param>
    /// <param name="halfLifeDays">The type's half-life in days.</param>
    public static double Score(double ageDays, double halfLifeDays) =>
        halfLifeDays <= 0 ? 0.0 : Math.Pow(0.5, Math.Max(0.0, ageDays) / halfLifeDays);

    /// <summary>
    /// SQL ORDER BY body for the <c>recency</c> sort: freshest-relative-to-its-type first. It orders by age (days
    /// since update) divided by the type's half-life, ascending — monotonic in <see cref="Score"/>, so SQLite needs
    /// no exp(). The type→half-life map is built from constants here (never user input), so it is injection-safe.
    /// </summary>
    public static string OrderByClause()
    {
        var cases = string.Join(" ", HalfLives.Select(entry => $"WHEN '{entry.Key}' THEN {Sql(entry.Value)}"));
        var halfLife = $"CASE n.type {cases} ELSE {Sql(DefaultHalfLifeDays)} END";
        return $"(julianday('now') - julianday(n.updated_utc)) / ({halfLife}) ASC";
    }

    private static string Sql(double value) => value.ToString("0.0###########", CultureInfo.InvariantCulture);
}
