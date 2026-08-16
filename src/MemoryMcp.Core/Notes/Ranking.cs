using System.Globalization;
using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Relative weights for the hybrid recall ranking signals (MEMP-174/175). The ranker blends each signal's
/// competition rank within the candidate pool via Reciprocal Rank Fusion (<c>w / (k + rank)</c>) — BM25 relevance,
/// recency, link-degree, an importance/pin boost, note type and project — plus the title match, which is scored
/// from its goodness instead of ranked because it takes too few distinct values to rank usefully (MEMP-239).
/// <para>The text signals outweigh the rest (MEMP-237). Equal weights made relevance one vote in six, so a note
/// with the query in its title lost to an off-topic note that was merely newer and of a more canonical type —
/// the field report had a BM25-rank-1 hit land second while a rank-16 note took the top slot. Lexical (3) and title (2)
/// now sum to 5 against the 4 non-text signals: what the user typed decides the order, and the rest breaks ties
/// or overturns it only when several of them agree.</para>
/// </summary>
/// <param name="Lexical">Weight of the BM25 relevance signal.</param>
/// <param name="Title">Weight of the title-match signal (MEMP-237; scored rather than ranked, see <see cref="TitleRelevance"/>).</param>
/// <param name="Recency">Weight of the newest-first signal.</param>
/// <param name="Link">Weight of the more-connected-first signal.</param>
/// <param name="Importance">Weight of the pinned/importance boost signal.</param>
/// <param name="Type">Weight of the per-type signal (canonical types above ephemeral ones).</param>
/// <param name="Project">Weight of the project-match signal (a requested project's notes lifted; MEMP-209).</param>
public sealed record RankingWeights(double Lexical = 3.0, double Title = 2.0, double Recency = 1.0, double Link = 1.0, double Importance = 1.0, double Type = 1.0, double Project = 1.0)
{
    /// <summary>The default blend: text relevance leads, the contextual signals follow.</summary>
    public static readonly RankingWeights Default = new();

    /// <summary>
    /// The blend used when a recall requests a project (MEMP-209): the project-match signal is up-weighted so a
    /// note in the asked-for project edges out an equally-relevant note from another project, without burying a
    /// genuinely more-relevant cross-project hit (the boost is a soft RRF signal, not a filter).
    /// </summary>
    public static readonly RankingWeights ProjectBoosted = Default with { Project = 2.0 };

    /// <summary>
    /// RRF damping constant (k): larger flattens the gap between top ranks. The common default of 60 flattened
    /// this pool to nothing — the top twelve fused scores of a real query spanned 0.005 in total, so no signal
    /// could actually order anything and ties fell through to arbitrary tiebreaks (MEMP-237). At 20 a one-rank
    /// difference is worth roughly three times as much, so the ranks separate.
    /// </summary>
    public const int K = 20;

    /// <summary>Largest candidate pool the hybrid ranker re-ranks; bounds the O(n²) rank computation and paging.</summary>
    public const int PoolSize = 200;
}

/// <summary>
/// Per-hit ranking explanation (MEMP-177): each signal's competition rank within the candidate pool plus the
/// fused score. Populated only when a recall/search is asked to explain. Lower rank = stronger on that signal
/// (1 = best); a fused score is higher-is-better.
/// </summary>
/// <param name="LexicalRank">Rank by BM25 relevance (1 = most relevant).</param>
/// <param name="TitleRank">Rank by title match (1 = title covers the query best; no-title pools tie at 1). Ordering
/// only: the title's contribution to <paramref name="Fused"/> is scored from its goodness rather than from this rank
/// (MEMP-239), so the gap between two title ranks says nothing about the gap between their contributions.</param>
/// <param name="RecencyRank">Rank by last-update time (1 = newest).</param>
/// <param name="LinkRank">Rank by link-degree (1 = most connected).</param>
/// <param name="ImportanceRank">Rank by pinned/importance (1 = most important; all-neutral pools tie at 1).</param>
/// <param name="TypeRank">Rank by per-type weight (1 = most canonical type).</param>
/// <param name="ProjectRank">Rank by project-match (1 = in the requested project; all tie at 1 when no project is requested).</param>
/// <param name="Fused">The fused RRF score (higher is better).</param>
public sealed record ScoreBreakdown(int LexicalRank, int TitleRank, int RecencyRank, int LinkRank, int ImportanceRank, int TypeRank, int ProjectRank, double Fused);

/// <summary>
/// One candidate in the hybrid re-rank pool: the result to return plus the raw signal values used to rank it.
/// Goodness is "higher is better" for every signal (BM25 is negated), so a single competition-rank routine
/// serves all four.
/// </summary>
/// <param name="Result">The search hit to return (score/snippet/payload already populated).</param>
/// <param name="Tier">Exact-key tier (0 = dedup_key match, 1 = title match, 2 = neither); kept ahead of the blend.</param>
/// <param name="Lexical">Lexical goodness (negated BM25; higher = more relevant).</param>
/// <param name="Title">Title goodness (query coverage of the title; higher = the title is more about the query).</param>
/// <param name="Recency">Recency goodness (Unix ms of the last update; higher = newer).</param>
/// <param name="Link">Link goodness (link-degree; higher = more connected).</param>
/// <param name="Importance">Importance goodness (pinned/importance; higher = more important).</param>
/// <param name="Type">Type goodness (canonical types higher than ephemeral ones).</param>
/// <param name="Project">Project goodness (1 when the note is in the requested project, else 0; MEMP-209).</param>
internal readonly record struct RankRow(SearchResult Result, int Tier, double Lexical, double Title, double Recency, double Link, double Importance, double Type, double Project);

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
        var titleRanks = CompetitionRanks(rows, row => row.Title);
        var recRanks = CompetitionRanks(rows, row => row.Recency);
        var linkRanks = CompetitionRanks(rows, row => row.Link);
        var impRanks = CompetitionRanks(rows, row => row.Importance);
        var typeRanks = CompetitionRanks(rows, row => row.Type);
        var projRanks = CompetitionRanks(rows, row => row.Project);

        var titleScale = TitleScale(rows.Count, weights.Title);
        var scored = new List<(SearchResult Result, int Tier, double Bm25, ScoreBreakdown Breakdown)>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var fused =
                (weights.Lexical / (RankingWeights.K + lexRanks[i])) +
                ((rows[i].Title / TitleRelevance.MaxGoodness) * titleScale) +
                (weights.Recency / (RankingWeights.K + recRanks[i])) +
                (weights.Link / (RankingWeights.K + linkRanks[i])) +
                (weights.Importance / (RankingWeights.K + impRanks[i])) +
                (weights.Type / (RankingWeights.K + typeRanks[i])) +
                (weights.Project / (RankingWeights.K + projRanks[i]));
            scored.Add((rows[i].Result, rows[i].Tier, rows[i].Result.Score,
                new ScoreBreakdown(lexRanks[i], titleRanks[i], recRanks[i], linkRanks[i], impRanks[i], typeRanks[i], projRanks[i], fused)));
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

    /// <summary>
    /// How much a full title match is worth, in fused-score units (MEMP-239). The title is the one signal that is
    /// scored rather than ranked: every other signal spreads across the pool — lexical relevance takes a distinct
    /// value on nearly every row — but a title either covers the query or it does not, so its goodness takes only a
    /// handful of values and its competition ranks bunch at the top (1, 2 and 4 out of 44 hits on the field query).
    /// Under <c>w / (k + rank)</c> that left the whole signal a 0.006 spread against lexical's 0.032, which is why
    /// the field report survived MEMP-237: a three-value rank cannot compete with a two-hundred-value one at any
    /// weight.
    /// <para>The scale is the pool's own lexical spread — the gap between the best and worst possible relevance
    /// contribution — so the title is measured against how much relevance actually varies HERE. It has to be
    /// relative: a fixed bonus that reads as modest against 44 hits is overwhelming in a pool of two, where first
    /// and second place differ by 0.002 and any flat addition decides the order by itself. Bounded this way, a
    /// title that fully covers the query can lift a note over one that merely mentions the word, and still cannot
    /// overturn a genuinely large margin in relevance.</para>
    /// </summary>
    private static double TitleScale(int poolSize, double weight) =>
        weight * ((1d / (RankingWeights.K + 1)) - (1d / (RankingWeights.K + Math.Max(poolSize, 1))));

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

    /// <summary>
    /// Project-match goodness (MEMP-209): 1 when the note's envelope <paramref name="project"/> equals the
    /// requested <paramref name="boostProject"/>, else 0. When no project is requested every note scores 0, so the
    /// signal ties the whole pool at rank 1 and contributes nothing — a no-op, exactly like an all-neutral pool.
    /// </summary>
    /// <param name="project">The note's envelope project (may be null).</param>
    /// <param name="boostProject">The project the recall asked to favour (may be null).</param>
    public static double ProjectGoodness(string? project, string? boostProject) =>
        boostProject is not null && string.Equals(project, boostProject, StringComparison.Ordinal) ? 1d : 0d;
}
