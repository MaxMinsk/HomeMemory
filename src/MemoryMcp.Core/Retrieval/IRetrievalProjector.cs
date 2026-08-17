namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// The content of a note that retrieval is allowed to see (MEMP-251) — deliberately not the full note, so a
/// projector cannot come to depend on envelope fields like status or timestamps.
/// </summary>
/// <param name="Type">Note type, which selects the mapping.</param>
/// <param name="Title">Note title.</param>
/// <param name="Body">Note body.</param>
/// <param name="TagsJson">Tags as a JSON array string.</param>
/// <param name="PayloadJson">Typed payload as JSON.</param>
public sealed record NoteContent(string Type, string? Title, string? Body, string? TagsJson, string? PayloadJson);

/// <summary>
/// A note's text sorted into the lanes the full-text index stores (MEMP-262).
/// <para>Which lane a field lands in is the TYPE's decision, declared by its schema. What each lane is WORTH is
/// a query-time decision, set by the ranking profile. Keeping those apart is what lets relevance be retuned
/// without rebuilding the index — the mistake Elasticsearch spent years undoing with index-time boost.</para>
/// </summary>
/// <param name="Primary">Text that says what the note is ABOUT.</param>
/// <param name="Secondary">Text that makes the note findable without being its subject.</param>
public sealed record LexicalLanes(string? Primary, string? Secondary);

/// <summary>
/// The single seam through which indexing code obtains a note's text (MEMP-251).
/// <para><b>Why this exists.</b> Before it, every indexer walked the payload itself and took every string it
/// found. That is defensible for full-text search, where an extra token is only noise, and wrong for embeddings,
/// where identifiers and URLs actively distort the vector and nothing can explain which field produced a hit.
/// Routing both through one projector means a type's indexing behaviour is declared once, in one place, rather
/// than re-implemented per subsystem — and it is why this abstraction has to exist BEFORE the first vector is
/// written, not after: retrofitting it means re-extracting and re-embedding the whole corpus with no record of
/// what changed.</para>
/// </summary>
public interface IRetrievalProjector
{
    /// <summary>Describes how a type is indexed, including the mapping hash that dates its stored passages.</summary>
    /// <param name="type">The note type.</param>
    RetrievalDescriptor Describe(string type);

    /// <summary>
    /// Every mapping hash that is current for SOME type — the set a stored passage must belong to before it can
    /// be trusted for scoring.
    /// <para>A set rather than one value, because once types carry their own mappings (MEMP-252) there is no
    /// single "current hash": a recipe passage and a fact passage are correctly stamped differently. Matching
    /// against one hash would silently drop every type but one from semantic recall.</para>
    /// </summary>
    IReadOnlyCollection<string> CurrentMappingHashes { get; }

    /// <summary>
    /// The note's full-text sources, in index order, each tagged with its JSON path.
    /// </summary>
    /// <param name="note">The note content.</param>
    IReadOnlyList<RetrievalText> Lexical(NoteContent note);

    /// <summary>
    /// The note split into units to embed, each carrying the paths it was built from. Empty when there is
    /// nothing worth embedding.
    /// </summary>
    /// <param name="note">The note content.</param>
    IReadOnlyList<RetrievalPassage> Passages(NoteContent note);

    /// <summary>
    /// The note's body and payload sorted into full-text lanes by their declared lexical role (MEMP-262).
    /// Title and tags are lanes of their own and are not returned here.
    /// </summary>
    /// <param name="note">The note content.</param>
    LexicalLanes Lanes(NoteContent note);
}
