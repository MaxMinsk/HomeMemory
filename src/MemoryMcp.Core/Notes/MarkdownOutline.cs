namespace MemoryMcp.Core.Notes;

/// <summary>
/// Extracts ATX Markdown headings (<c>#</c>…<c>######</c>) from a body with their character offsets.
/// Headings inside fenced code blocks (<c>```</c> / <c>~~~</c>) are ignored. Pure string scan — no regex.
/// </summary>
internal static class MarkdownOutline
{
    public static IReadOnlyList<NoteHeading> Parse(string? body)
    {
        var headings = new List<NoteHeading>();
        if (string.IsNullOrEmpty(body))
        {
            return headings;
        }

        var offset = 0;
        var inFence = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence)
            {
                var level = HeadingLevel(trimmed);
                if (level > 0)
                {
                    headings.Add(new NoteHeading(level, trimmed[level..].Trim(), offset));
                }
            }

            offset += line.Length + 1; // + the '\n' that Split removed
        }

        return headings;
    }

    // An ATX heading is 1–6 leading '#' followed by a space (or the whole line). Returns 0 if not a heading.
    private static int HeadingLevel(string trimmed)
    {
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        var isHeading = hashes is >= 1 and <= 6 && (hashes == trimmed.Length || trimmed[hashes] == ' ');
        return isHeading ? hashes : 0;
    }
}
