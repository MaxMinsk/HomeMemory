using System.Text.Json;

namespace MemoryMcp.Core.Skills;

/// <summary>
/// A skill chosen for the task at hand, with the reason it was chosen (MEMP-257).
/// <para>The reason is not decoration. An instruction that arrives without saying why it is here is
/// indistinguishable from one the agent invented, and an agent that cannot tell those apart cannot decline
/// the wrong one.</para>
/// </summary>
/// <param name="Skill">The skill, including its body when activated.</param>
/// <param name="Reason">Why this skill was selected for this query.</param>
public sealed record SelectedSkill(Skill Skill, string Reason);

/// <summary>
/// Picks the few skills a task actually needs out of the whole catalogue (MEMP-257).
/// <para><b>The problem this solves.</b> memory_context listed every skill in scope with every body null. So a
/// caller paid for a catalogue it did not ask for, learned nothing it could act on, and still had to make a
/// second call to get any instruction. Observed live: an architecture task was offered <c>frontend-design</c>.</para>
/// <para><b>Deliberately simple.</b> Lexical scoring over key, title, summary and tags — no vector index for
/// skills, no trust state, no activation ledger. With a catalogue of this size the question is whether simple
/// matching is already enough, and the honest way to find out is to ship the simple thing and look at where it
/// is wrong (MEMP-260). Anything cleverer should be justified by a specific repeated failure.</para>
/// </summary>
public static class SkillSelector
{
    /// <summary>How many descriptors to offer. Enough to choose from, few enough to read.</summary>
    public const int MaxCandidates = 5;

    /// <summary>
    /// How many bodies to deliver. Two is the cap because a task with three genuinely applicable instruction
    /// sets is a task that should be split, and because instructions crowd out the recall they exist to support.
    /// </summary>
    public const int MaxActivated = 2;

    /// <summary>Total characters of activated instruction, so the skills section cannot swamp the block.</summary>
    public const int ActivationBudgetChars = 12_000;

    /// <summary>A skill must clear this to be offered at all; below it, the match is coincidence.</summary>
    private const int CandidateThreshold = 6;

    /// <summary>And this to be activated without being asked for by name — a stronger claim than "relevant".</summary>
    private const int ActivationThreshold = 18;

    /// <summary>
    /// Ranks <paramref name="available"/> against the task query. Returns descriptors to offer (bodies stripped)
    /// and the few to activate (bodies kept).
    /// </summary>
    /// <param name="available">Every skill in scope, already resolved across project/domain/commons.</param>
    /// <param name="query">The task query; null or blank selects nothing.</param>
    /// <param name="project">The task's project, if any.</param>
    /// <param name="loadBody">Fetches a skill's body when it is activated.</param>
    public static (IReadOnlyList<Skill> Candidates, IReadOnlyList<SelectedSkill> Activated) Select(
        IReadOnlyList<Skill> available, string? query, string? project, Func<Skill, Skill?> loadBody)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(loadBody);

        // With no query there is nothing to be relevant TO. Offering the catalogue anyway is what this replaced.
        if (string.IsNullOrWhiteSpace(query) || available.Count == 0)
        {
            return (Array.Empty<Skill>(), Array.Empty<SelectedSkill>());
        }

        var tokens = Words(query).ToHashSet(StringComparer.Ordinal);
        var scored = available
            .Select(skill => (Skill: skill, Score: Score(skill, query!, tokens, project)))
            .Where(entry => entry.Score >= CandidateThreshold)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Skill.Key, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();

        var candidates = scored.Select(entry => entry.Skill with { Body = null }).ToList();
        var activated = new List<SelectedSkill>();
        var budget = ActivationBudgetChars;
        foreach (var entry in scored.Where(entry => entry.Score >= ActivationThreshold).Take(MaxActivated))
        {
            var full = loadBody(entry.Skill);
            if (full?.Body is not { Length: > 0 } body || body.Length > budget)
            {
                continue;
            }

            budget -= body.Length;
            activated.Add(new SelectedSkill(full, Reason(entry.Skill, tokens, project)));
        }

        return (candidates, activated);
    }

    // Weights are ordered by how much each field CLAIMS. A key named in the query is a request; a tag is a
    // curated intent; a title is a label; a summary is prose that mentions many things in passing.
    private static int Score(Skill skill, string query, IReadOnlySet<string> tokens, string? project)
    {
        var score = 0;
        if (MentionsKey(query, skill.Key))
        {
            score += 40;
        }

        score += 9 * Overlap(skill.Title, tokens);
        score += 5 * Overlap(skill.Summary, tokens);
        score += 8 * TagOverlap(skill, tokens);

        // A project's own override of a key is more likely to be the right one when that project is the task's.
        if (skill.Project is not null && string.Equals(skill.Project, project, StringComparison.Ordinal))
        {
            score += 6;
        }
        else if (skill.Project is not null)
        {
            // A skill belonging to a DIFFERENT project is almost never what this task wants, whatever it says.
            score -= 12;
        }

        if (skill.TargetType is { Length: > 0 } target && tokens.Contains(target.ToLowerInvariant()))
        {
            score += 6;
        }

        return score;
    }

    private static string Reason(Skill skill, IReadOnlySet<string> tokens, string? project)
    {
        if (skill.Project is not null && string.Equals(skill.Project, project, StringComparison.Ordinal))
        {
            return $"this project's own '{skill.Key}'";
        }

        var matched = Words(skill.Title).Concat(TagWords(skill)).Where(tokens.Contains)
            .Distinct(StringComparer.Ordinal).Take(3).ToList();
        return matched.Count > 0
            ? $"matches the task on {string.Join(", ", matched)}"
            : $"'{skill.Key}' is the closest match for this task";
    }

    // A key is written lower-kebab; a person asks for it in words. Compare on both so "sprint release" finds
    // sprint-release without a special case for every skill.
    private static bool MentionsKey(string query, string key) =>
        query.Contains(key, StringComparison.OrdinalIgnoreCase)
        || query.Contains(key.Replace('-', ' '), StringComparison.OrdinalIgnoreCase);

    private static int Overlap(string? text, IReadOnlySet<string> tokens) =>
        Words(text).Distinct(StringComparer.Ordinal).Count(tokens.Contains);

    private static int TagOverlap(Skill skill, IReadOnlySet<string> tokens) =>
        TagWords(skill).Distinct(StringComparer.Ordinal).Count(tokens.Contains);

    // Tags are namespaced (intent:release, area:frontend); the namespace is bookkeeping and the value is the
    // word a person would actually type, so both halves are compared.
    private static IEnumerable<string> TagWords(Skill skill)
    {
        foreach (var tag in Tags(skill))
        {
            foreach (var word in Words(tag.Replace(':', ' ')))
            {
                yield return word;
            }
        }
    }

    private static IReadOnlyList<string> Tags(Skill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.TagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(skill.TagsJson!) ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    // Lowercased alphanumeric words of length >= 3, matching how rule triggers are tokenised elsewhere.
    private static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var current = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (current.Length >= 3)
            {
                yield return current.ToString();
            }

            current.Clear();
        }

        if (current.Length >= 3)
        {
            yield return current.ToString();
        }
    }
}
