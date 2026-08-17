namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// One piece of a note's text, paired with the JSON path it came from (MEMP-251).
/// <para>The path is what makes retrieval explainable and selectively re-indexable: it lets a hit say which
/// field produced it, lets a mapping name the fields worth embedding, and lets a mapping change invalidate only
/// the passages it actually affects.</para>
/// </summary>
/// <param name="Path">Where the text came from: <c>title</c>, <c>body</c>, <c>tags[0]</c>,
/// <c>payload.statement</c>, <c>payload.ingredients[3].name</c>.</param>
/// <param name="Text">The text itself, verbatim.</param>
public sealed record RetrievalText(string Path, string Text);

/// <summary>
/// A unit of text to embed (MEMP-251). A note yields several, and it scores as its BEST one — measured to beat
/// both a title-only index and one mean-pooled vector per note (MEMP-242).
/// </summary>
/// <param name="Name">The passage group, e.g. <c>title</c> or <c>text</c>.</param>
/// <param name="Ordinal">Position within the group, so a passage is addressable and re-indexable.</param>
/// <param name="Text">The text to embed.</param>
/// <param name="SourcePaths">The JSON paths this passage was built from.</param>
public sealed record RetrievalPassage(string Name, int Ordinal, string Text, IReadOnlyList<string> SourcePaths);

/// <summary>
/// How one note type is turned into indexable text (MEMP-251) — the compiled result of a type's data schema
/// plus its retrieval mapping.
/// <para><b>Why the version and hash are separate from the schema version.</b> A data-contract change and an
/// indexing change have different costs: the first invalidates validation, the second invalidates the index.
/// Keeping the mapping hash apart lets extraction be retuned without publishing a new type version, and lets a
/// change mark exactly the affected vectors stale instead of forcing a full rebuild.</para>
/// </summary>
/// <param name="Type">The note type this describes.</param>
/// <param name="SchemaVersion">The data schema version it was compiled against (0 when the type has none).</param>
/// <param name="MappingVersion">Human-readable mapping identity, e.g. <c>legacy@1</c>.</param>
/// <param name="MappingHash">Stable hash of the mapping; a change means stored passages are stale.</param>
/// <param name="IsLegacy">True when this type has no declared mapping and falls back to indexing everything.</param>
public sealed record RetrievalDescriptor(
    string Type, int SchemaVersion, string MappingVersion, string MappingHash, bool IsLegacy);
