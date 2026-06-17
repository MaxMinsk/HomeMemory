using MemoryMcp.Core.Skills;

namespace MemoryMcp.Core.Notes;

/// <summary>A prompt-ready context block for a task (MEMP-137): the layered memory an agent should load up
/// front — the rules in force, the relevant skills, and a recall of notes related to the query — assembled
/// in one call instead of several search/recall/skill_list round-trips.</summary>
/// <param name="Domain">The domain the block was assembled for.</param>
/// <param name="Rules">Active <c>memory_rule</c> notes (domain + commons), most important first (always_apply, then priority).</param>
/// <param name="Skills">Skills guiding the domain (and commons), metadata only.</param>
/// <param name="Recall">FTS hits for the query plus their one-hop linked neighbors.</param>
/// <param name="Policy">A reminder that memory is advisory — the live user/data wins over stored notes.</param>
/// <param name="Warnings">Assembly notes, e.g. truncation of the rule set.</param>
public sealed record ContextBlock(
    string Domain,
    IReadOnlyList<SearchResult> Rules,
    IReadOnlyList<Skill> Skills,
    RecallResult Recall,
    string Policy,
    IReadOnlyList<string> Warnings);
