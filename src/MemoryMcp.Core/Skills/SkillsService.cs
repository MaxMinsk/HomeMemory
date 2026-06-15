using System.Text.Json;
using MemoryMcp.Core.Notes;

namespace MemoryMcp.Core.Skills;

/// <summary>
/// Authoring and retrieval of server-hosted skills. A skill is just a note of type <c>skill</c>
/// (consistent with the single-table design), so it reuses validation, audit and soft-delete.
/// Skills are the shared conventions agents consult so they author each note type consistently.
/// </summary>
public sealed class SkillsService
{
    /// <summary>The note <c>type</c> under which skills are stored.</summary>
    public const string SkillType = "skill";

    private readonly NotesRepository _notes;

    /// <summary>Creates the service over the note repository.</summary>
    /// <param name="notes">Backing note repository.</param>
    public SkillsService(NotesRepository notes) => _notes = notes;

    /// <summary>Creates or updates a skill (idempotent by key). The body holds the skill text.</summary>
    public Skill Upsert(string domain, string key, string? title, string body,
        string? targetType, int version, string? summary, string? sourceAgent)
    {
        var payload = new Dictionary<string, object?> { ["key"] = key, ["version"] = version };
        if (!string.IsNullOrWhiteSpace(targetType))
        {
            payload["target_type"] = targetType;
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            payload["summary"] = summary;
        }

        _notes.Upsert(domain, SkillType, title, body, JsonSerializer.Serialize(payload), null, key, sourceAgent ?? "skill-author");
        return new Skill(key, title, targetType, version, summary, body);
    }

    /// <summary>Lists skills in a domain (metadata only, no body), optionally narrowed to those guiding <paramref name="targetType"/>.</summary>
    public IReadOnlyList<Skill> List(string domain, string? targetType = null) =>
        _notes.List(domain, SkillType)
            .Select(ToSkill)
            .Where(skill => targetType is null || string.Equals(skill.TargetType, targetType, StringComparison.Ordinal))
            .Select(skill => skill with { Body = null })
            .ToList();

    /// <summary>Returns the full skill (including body) for a key, or <c>null</c>.</summary>
    public Skill? Get(string domain, string key)
    {
        var note = _notes.GetByDedupKey(domain, SkillType, key);
        return note is null ? null : ToSkill(note);
    }

    private static Skill ToSkill(Note note)
    {
        using var document = JsonDocument.Parse(note.PayloadJson ?? "{}");
        var root = document.RootElement;

        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;

        return new Skill(Text("key") ?? note.DedupKey ?? string.Empty, note.Title, Text("target_type"), version, Text("summary"), note.Body);
    }
}
