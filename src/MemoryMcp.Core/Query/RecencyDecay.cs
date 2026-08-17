using System.Globalization;

namespace MemoryMcp.Core.Query;

/// <summary>
/// Type-aware recency decay (MEMP-037): a note's freshness fades with age, with a HALF-LIFE that depends on its
/// type — ephemeral types (episode, journal) fade in days, durable knowledge (recipe, reference, skill) effectively
/// never. Powers the <c>recency</c> sort, which orders by age normalized to each type's half-life, so a fresh
/// ephemeral note and an old durable note are compared fairly (a week-old journal sinks below a year-old recipe).
/// <see cref="Score"/> exposes the 0..1 decay multiplier for other rankers. The per-TYPE half-life moved to
/// <c>TypePolicy</c> in MEMP-253 — it is a property of the type, declared by its schema, not of the decay curve.
/// </summary>
public static class RecencyDecay
{
    /// <summary>Half-life (days) used for a type with no specific entry.</summary>
    public const double DefaultHalfLifeDays = 90.0;

    /// <summary>The decay multiplier in (0, 1]: 1 when brand new, 0.5 at one half-life, approaching 0 as it ages.</summary>
    /// <param name="ageDays">Age of the note in days (negative is treated as 0).</param>
    /// <param name="halfLifeDays">The type's half-life in days.</param>
    public static double Score(double ageDays, double halfLifeDays) =>
        halfLifeDays <= 0 ? 0.0 : Math.Pow(0.5, Math.Max(0.0, ageDays) / halfLifeDays);

}
