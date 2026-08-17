namespace MemoryMcp.Core.Notes;

/// <summary>
/// The per-column BM25 weighting used for every FTS query (MEMP-237). <c>bm25(notes_fts)</c> with no arguments
/// weights all columns equally, so a note's <b>title</b> counted for no more than a passing mention buried in a
/// long body — searching one word ranked the notes that name it in their title below notes that merely mention it.
/// A title is the strongest statement of what a note is about, so it carries a ×5 weight here.
/// <para>The <c>stems</c> sidecar re-indexes title/body/tags/payload text in stemmed form; it is deliberately
/// left at ×1 so it keeps only ADDING recall (word forms) without competing with the raw signal.</para>
/// <para>The argument order must track the FTS5 column order declared by the current FTS migration:
/// <c>title, body, tags, dedup_key, payload, stems</c>. Change that table and this expression changes with it.</para>
/// </summary>
internal static class Bm25Weights
{
    /// <summary>The weighted BM25 score expression (lower = more relevant, as FTS5 defines it).</summary>
    public const string Expression = "bm25(notes_fts, " + Identity + ", " + Title + ", " + PrimaryText + ", "
        + SecondaryText + ", " + Tags + ", " + Stems + ")";

    // Lane weights, in the order the FTS table declares them (MEMP-262). These are the values the old
    // per-source columns carried, kept deliberately unchanged so introducing lanes could not move any ranking:
    // the migration and the re-weighting are separate decisions, and only the second one needs measuring.
    private const string Identity = "1.0";
    private const string Title = "5.0";
    private const string PrimaryText = "1.0";
    private const string SecondaryText = "1.0";
    private const string Tags = "1.0";
    private const string Stems = "1.0";
}
