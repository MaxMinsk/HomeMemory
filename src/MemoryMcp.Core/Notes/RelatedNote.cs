namespace MemoryMcp.Core.Notes;

/// <summary>
/// A note suggested as related to another (a linking/dedup hint, not a link). It can surface by shared tags,
/// lexical similarity, or a direct link (MEMP-178); <see cref="Reasons"/> says which, strongest signal first.
/// </summary>
/// <param name="Id">The candidate note id.</param>
/// <param name="Title">Its title, if any.</param>
/// <param name="Type">Its type.</param>
/// <param name="Domain">Its domain.</param>
/// <param name="SharedTags">The tags it shares with the source note (empty when it surfaced only by text/link).</param>
/// <param name="Reasons">Why it surfaced: any of <c>tags</c>, <c>text</c>, <c>link</c>.</param>
public sealed record RelatedNote(
    string Id, string? Title, string Type, string Domain,
    IReadOnlyList<string> SharedTags, IReadOnlyList<string> Reasons);
