using System.Text;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// Splits a note's text into the overlapping windows that get embedded (MEMP-242, shared by MEMP-252).
/// <para>Shared deliberately. Both projectors must window identically, or the schema-driven arm of the
/// three-arm measurement would be comparing chunking as well as field selection, and a difference could not be
/// attributed to either.</para>
/// </summary>
internal static class PassageWindows
{
    /// <summary>Window size in characters, and its overlap — both measured against the golden set (MEMP-242).</summary>
    internal const int PassageChars = 320;
    internal const int PassageOverlap = 80;

    /// <summary>Below this a window carries no usable meaning and is dropped rather than embedded.</summary>
    internal const int MinPassageChars = 40;

    /// <summary>
    /// Appends windows over <paramref name="parts"/>, each prefixed with the title so a window keeps its
    /// subject, and each carrying the JSON paths it was built from.
    /// </summary>
    /// <param name="passages">Destination.</param>
    /// <param name="name">Passage group name.</param>
    /// <param name="title">Title to lead each window with, if any.</param>
    /// <param name="parts">The text to window, in index order.</param>
    internal static void Add(List<RetrievalPassage> passages, string name, string? title, IReadOnlyList<RetrievalText> parts)
    {
        var joined = new StringBuilder();
        var offsets = new List<(int Start, string Path)>();
        foreach (var text in parts)
        {
            offsets.Add((joined.Length, text.Path));
            joined.Append(text.Text.Replace('\n', ' ')).Append(' ');
        }

        var all = joined.ToString();
        var lead = string.IsNullOrWhiteSpace(title) ? string.Empty : title + ". ";
        var ordinal = 0;
        for (var start = 0; start < all.Length; start += PassageChars - PassageOverlap)
        {
            var window = all.Substring(start, Math.Min(PassageChars, all.Length - start)).Trim();
            // The length floor exists to drop a meaningless trailing remnant, not to skip short notes: a note
            // whose whole text is under the floor must still be embedded, or every brief note in the corpus
            // would be searchable by its title alone.
            if (window.Length == 0 || (window.Length < MinPassageChars && ordinal > 0))
            {
                continue;
            }

            var end = start + PassageChars;
            var paths = offsets.Where(offset => offset.Start < end).Select(offset => offset.Path)
                .Distinct(StringComparer.Ordinal).ToList();
            passages.Add(new RetrievalPassage(name, ordinal++, lead + window, paths));
        }
    }
}
