namespace MemoryMcp.Core.Notes;

/// <summary>
/// Per-agent memory-adoption signals (MEMP-207): how much an agent reads (recall/search) versus writes, so an
/// operator can see "who writes without reading first". Writes come from the append-only event log
/// (<c>note_events.actor</c>, all-time, scope-restricted); reads from <c>agent_reads</c> (identified reads only,
/// global). <see cref="WritesWithoutReading"/> is the headline flag.
/// </summary>
/// <param name="Agent">The agent identity (sourceAgent); "(unknown)" for writes with no recorded actor.</param>
/// <param name="Reads">Recall/search reads recorded for this agent (identified reads only).</param>
/// <param name="Writes">Total write events (create + update + patch + ...).</param>
/// <param name="Creates">New-note writes.</param>
/// <param name="Updates">Whole-note upsert updates.</param>
/// <param name="Patches">Partial (notes_patch) updates.</param>
/// <param name="Projects">The workspaces this agent wrote to (project, or the domain when a note has no project),
/// heaviest first — so it's obvious WHAT the agent worked on, not just how much (MEMP-229).</param>
public sealed record AgentAdoption(
    string Agent, long Reads, long Writes, long Creates, long Updates, long Patches,
    IReadOnlyList<AgentWorkspace>? Projects = null)
{
    /// <summary>True when the agent wrote but has no recorded reads — the "writes without reading" signal.</summary>
    public bool WritesWithoutReading => Writes > 0 && Reads == 0;
}

/// <summary>One workspace an agent wrote to (MEMP-229): a project slug, or the domain when the note has no
/// project, with how many write events landed there.</summary>
/// <param name="Name">The project slug (or domain when the note carries no project).</param>
/// <param name="Writes">Write events this agent made in that workspace.</param>
public sealed record AgentWorkspace(string Name, long Writes);

/// <summary>
/// The memory-adoption report (MEMP-207): one <see cref="AgentAdoption"/> row per agent, plus rollup totals.
/// Ordered by writes descending so the heaviest writers surface first.
/// </summary>
/// <param name="Agents">Per-agent adoption rows, heaviest writers first.</param>
/// <param name="TotalReads">Sum of recorded reads across agents.</param>
/// <param name="TotalWrites">Sum of write events across agents.</param>
public sealed record AdoptionReport(IReadOnlyList<AgentAdoption> Agents, long TotalReads, long TotalWrites);
