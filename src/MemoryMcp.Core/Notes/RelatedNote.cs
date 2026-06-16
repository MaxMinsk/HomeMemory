namespace MemoryMcp.Core.Notes;

/// <summary>A note suggested as related to another by shared tags (a linking/dedup hint, not a link).</summary>
/// <param name="Id">The candidate note id.</param>
/// <param name="Title">Its title, if any.</param>
/// <param name="Type">Its type.</param>
/// <param name="Domain">Its domain.</param>
/// <param name="SharedTags">The tags it shares with the source note (the reason it surfaced).</param>
public sealed record RelatedNote(string Id, string? Title, string Type, string Domain, IReadOnlyList<string> SharedTags);
