using System.Globalization;
using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Relative weights for the hybrid recall ranking signals (MEMP-174/175). The ranker blends each signal's
/// competition rank within the candidate pool via Reciprocal Rank Fusion (<c>w / (k + rank)</c>) — lexical
/// relevance (BM25), recency, link-degree and an importance/pin boost. Defaults weight every signal equally.
/// </summary>
/// <param name="Lexical">Weight of the BM25 relevance signal.</param>
/// <param name="Recency">Weight of the newest-first signal.</param>
/// <param name="Link">Weight of the more-connected-first signal.</param>
/// <param name="Importance">Weight of the pinned/importance boost signal.</param>
/// <param name="Type">Weight of the per-type signal (canonical types above ephemeral ones).</param>
public sealed record RankingWeights(double Lexical = 1.0, double Recency = 1.0, double Link = 1.0, double Importance = 1.0, double Type = 1.0)
{
    /// <summary>The default equal-weight blend.</summary>
    public static readonly RankingWeights Default = new();

    /// <summary>RRF damping constant (k): larger flattens the gap between top ranks. 60 is the common default.</summary>
    public const int K = 60;

    /// <summary>Largest candidate pool the hybrid ranker re-ranks; bounds the O(n²) rank computation and paging.</summary>
    public const int PoolSize = 200;
}

/// <summary>
/// Per-hit ranking explanation (MEMP-177): each signal's competition rank within the candidate pool plus the
/// fused score. Populated only when a recall/search is asked to explain. Lower rank = stronger on that signal
/// (1 = best); a fused score is higher-is-better.
/// </summary>
/// <param name="LexicalRank">Rank by BM25 relevance (1 = most relevant).</param>
/// <param name="RecencyRank">Rank by last-update time (1 = newest).</param>
/// <param name="LinkRank">Rank by link-degree (1 = most connected).</param>
/// <param name="ImportanceRank">Rank by pinned/importance (1 = most important; all-neutral pools tie at 1).</param>
/// <param name="TypeRank">Rank by per-type weight (1 = most canonical type).</param>
/// <param name="Fused">The fused RRF score (higher is better).</param>
public sealed record ScoreBreakdown(int LexicalRank, int RecencyRank, int LinkRank, int ImportanceRank, int TypeRank, double Fused);

/// <summary>
/// One candidate in the hybrid re-rank pool: the result to return plus the raw signal values used to rank it.
/// Goodness is "higher is better" for every signal (BM25 is negated), so a single competition-rank routine
/// serves all four.
/// </summary>
/// <param name="Result">The search hit to return (score/snippet/payload already populated).</param>
/// <param name="Tier">Exact-key tier (0 = dedup_key match, 1 = title match, 2 = neither); kept ahead of the blend.</param>
/// <param name="Lexical">Lexical goodness (negated BM25; higher = more relevant).</param>
/// <param name="Recency">Recency goodness (Unix ms of the last update; higher = newer).</param>
/// <param name="Link">Link goodness (link-degree; higher = more connected).</param>
/// <param name="Importance">Importance goodness (pinned/importance; higher = more important).</param>
/// <param name="Type">Type goodness (canonical types higher than ephemeral ones).</param>
internal readonly record struct RankRow(SearchResult Result, int Tier, double Lexical, double Recency, double Link, double Importance, double Type);

/// <summary>Reciprocal-rank-fusion re-ranker over a bounded candidate pool (MEMP-174). Pure and deterministic.</summary>
internal static class HybridRanker
{
    /// <summary>
    /// Fuses the pool's signals into a single order: exact-key tier first, then RRF-blended relevance. Returns each
    /// surviving row paired with its <see cref="ScoreBreakdown"/> (always computed; the caller decides whether to surface it).
    /// </summary>
    public static List<(SearchResult Result, ScoreBreakdown Breakdown)> Fuse(IReadOnlyList<RankRow> rows, RankingWeights weights)
    {
        var lexRanks = CompetitionRanks(rows, row => row.Lexical);
        var recRanks = CompetitionRanks(rows, row => row.Recency);
        var linkRanks = CompetitionRanks(rows, row => row.Link);
        var impRanks = CompetitionRanks(rows, row => row.Importance);
        var typeRanks = CompetitionRanks(rows, row => row.Type);

        var scored = new List<(SearchResult Result, int Tier, double Bm25, ScoreBreakdown Breakdown)>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var fused =
                (weights.Lexical / (RankingWeights.K + lexRanks[i])) +
                (weights.Recency / (RankingWeights.K + recRanks[i])) +
                (weights.Link / (RankingWeights.K + linkRanks[i])) +
                (weights.Importance / (RankingWeights.K + impRanks[i])) +
                (weights.Type / (RankingWeights.K + typeRanks[i]));
            scored.Add((rows[i].Result, rows[i].Tier, rows[i].Result.Score,
                new ScoreBreakdown(lexRanks[i], recRanks[i], linkRanks[i], impRanks[i], typeRanks[i], fused)));
        }

        // Exact-key matches stay on top; then strongest fused score; BM25 then id break ties for a stable order.
        scored.Sort((a, b) =>
        {
            var byTier = a.Tier.CompareTo(b.Tier);
            if (byTier != 0)
            {
                return byTier;
            }

            var byFused = b.Breakdown.Fused.CompareTo(a.Breakdown.Fused);
            if (byFused != 0)
            {
                return byFused;
            }

            var byBm25 = a.Bm25.CompareTo(b.Bm25); // lower BM25 = more relevant
            return byBm25 != 0 ? byBm25 : string.CompareOrdinal(a.Result.Id, b.Result.Id);
        });

        return scored.Select(item => (item.Result, item.Breakdown)).ToList();
    }

    // Competition rank ("1224"): rank = 1 + how many pool items are strictly better. Ties share a rank, so a pool
    // where every item is equal on a signal (e.g. no note carries importance) ranks them all 1 — a no-op contribution.
    private static int[] CompetitionRanks(IReadOnlyList<RankRow> rows, Func<RankRow, double> goodness)
    {
        var values = new double[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            values[i] = goodness(rows[i]);
        }

        var ranks = new int[rows.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var better = 0;
            foreach (var other in values)
            {
                if (other > values[i])
                {
                    better++;
                }
            }

            ranks[i] = better + 1;
        }

        return ranks;
    }

    /// <summary>Recency goodness: Unix milliseconds of an ISO timestamp (higher = newer); 0 when unparseable.</summary>
    public static double RecencyGoodness(string? updatedUtc)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.TryParse(updatedUtc, CultureInfo.InvariantCulture, styles, out var when)
            ? when.ToUnixTimeMilliseconds()
            : 0d;
    }

    /// <summary>
    /// Importance goodness from a note's payload and tags: <c>pinned</c> dominates, then <c>importance</c>. Read from
    /// <c>payload.pinned</c>/<c>payload.importance</c> (open-payload types) and, universally, from a <c>pinned</c> tag
    /// or an <c>importance:N</c> tag — so any note (even one whose schema forbids extra payload) can be lifted. A note
    /// with no such signal scores 0, leaving a no-importance pool's ranking untouched (MEMP-175).
    /// </summary>
    public static double ImportanceGoodness(string? payloadJson, string? tagsJson)
    {
        var pinned = false;
        var importance = 0d;

        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    pinned = root.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("importance", out var imp) && imp.ValueKind == JsonValueKind.Number && imp.TryGetDouble(out var value))
                    {
                        importance = value;
                    }
                }
            }
            catch (JsonException)
            {
                // payload not an object / malformed — fall through to tag signals
            }
        }

        if (!string.IsNullOrEmpty(tagsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(tagsJson);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in document.RootElement.EnumerateArray())
                    {
                        if (tag.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var value = tag.GetString();
                        if (string.Equals(value, "pinned", StringComparison.OrdinalIgnoreCase))
                        {
                            pinned = true;
                        }
                        else if (value is not null && value.StartsWith("importance:", StringComparison.OrdinalIgnoreCase)
                            && double.TryParse(value.AsSpan("importance:".Length), NumberStyles.Number, CultureInfo.InvariantCulture, out var tagImportance))
                        {
                            importance = Math.Max(importance, tagImportance);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // tags not an array / malformed — ignore
            }
        }

        return (pinned ? 1_000_000d : 0d) + importance;
    }

    // Canonical, durable knowledge ranks above ephemeral logs at equal relevance (MEMP-193).
    private static readonly HashSet<string> CanonicalTypes = new(StringComparer.Ordinal)
    {
        "memory_rule", "skill", "reference", "recipe", "decision", "project_state", "preference", "saved_search",
    };

    private static readonly HashSet<string> EphemeralTypes = new(StringComparer.Ordinal) { "journal", "episode" };

    /// <summary>Per-type goodness: canonical knowledge (2) &gt; ordinary notes (1) &gt; ephemeral logs (0).</summary>
    /// <param name="type">The note type.</param>
    public static double TypeGoodness(string type) =>
        CanonicalTypes.Contains(type) ? 2d : EphemeralTypes.Contains(type) ? 0d : 1d;
}
