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
public sealed record AgentAdoption(string Agent, long Reads, long Writes, long Creates, long Updates, long Patches)
{
    /// <summary>True when the agent wrote but has no recorded reads — the "writes without reading" signal.</summary>
    public bool WritesWithoutReading => Writes > 0 && Reads == 0;
}

/// <summary>
/// The memory-adoption report (MEMP-207): one <see cref="AgentAdoption"/> row per agent, plus rollup totals.
/// Ordered by writes descending so the heaviest writers surface first.
/// </summary>
/// <param name="Agents">Per-agent adoption rows, heaviest writers first.</param>
/// <param name="TotalReads">Sum of recorded reads across agents.</param>
/// <param name="TotalWrites">Sum of write events across agents.</param>
public sealed record AdoptionReport(IReadOnlyList<AgentAdoption> Agents, long TotalReads, long TotalWrites);
