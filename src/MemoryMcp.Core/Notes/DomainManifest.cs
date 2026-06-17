using MemoryMcp.Core.Skills;

namespace MemoryMcp.Core.Notes;

/// <summary>A compact orientation for one domain (MEMP-139): what kinds of notes it holds, the skills that
/// guide it, and the active rules in force — so an agent entering a domain gets its shape in one call
/// instead of dumping everything.</summary>
/// <param name="Domain">The domain (namespace) this manifest describes.</param>
/// <param name="NotesByType">Active note counts grouped by type.</param>
/// <param name="Skills">The domain's skills (metadata only, no bodies).</param>
/// <param name="Rules">Active <c>memory_rule</c> notes in the domain (with payload), the rules in force.</param>
public sealed record DomainManifest(
    string Domain,
    IReadOnlyDictionary<string, long> NotesByType,
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<SearchResult> Rules);
