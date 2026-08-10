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
    public const string Expression = "bm25(notes_fts, 5.0, 1.0, 1.0, 1.0, 1.0, 1.0)";
}
