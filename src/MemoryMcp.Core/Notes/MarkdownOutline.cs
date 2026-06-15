namespace MemoryMcp.Core.Notes;

/// <summary>
/// Extracts ATX Markdown headings (<c>#</c>…<c>######</c>) from a body with their character offsets.
/// Headings inside fenced code blocks (<c>```</c> / <c>~~~</c>) are ignored. Pure string scan — no regex.
/// </summary>
internal static class MarkdownOutline
{
    public static IReadOnlyList<NoteHeading> Parse(string? body)
    {
        var scanned = Scan(body);
        var total = body?.Length ?? 0;

        var headings = new List<NoteHeading>(scanned.Count);
        for (var i = 0; i < scanned.Count; i++)
        {
            var (level, text, offset) = scanned[i];
            var end = total;
            for (var j = i + 1; j < scanned.Count; j++)
            {
                if (scanned[j].Level <= level) // next section at the same or a higher level closes this one
                {
                    end = scanned[j].Offset;
                    break;
                }
            }

            headings.Add(new NoteHeading(level, text, offset, end));
        }

        return headings;
    }

    // Finds heading lines (level, text, line-start offset), skipping fenced code blocks.
    private static List<(int Level, string Text, int Offset)> Scan(string? body)
    {
        var scanned = new List<(int Level, string Text, int Offset)>();
        if (string.IsNullOrEmpty(body))
        {
            return scanned;
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
                    scanned.Add((level, trimmed[level..].Trim(), offset));
                }
            }

            offset += line.Length + 1; // + the '\n' that Split removed
        }

        return scanned;
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
