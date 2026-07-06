namespace MemoryMcp.Core.Notes;

/// <summary>
/// Toggle for the advisory write-time "adoption" hints (MEMP-204/205): the recall-before-write nudge and the
/// post-write related-notes hint. Both are advisory and never block a write. Read from configuration
/// (env <c>MEMORY_ADOPTION_HINTS</c>); on unless explicitly disabled.
/// </summary>
/// <param name="Enabled">Master switch for the write-time adoption hints (nudge + related).</param>
public sealed record AdoptionOptions(bool Enabled = true)
{
    /// <summary>Reads <c>MEMORY_ADOPTION_HINTS</c>; on unless it is explicitly <c>false</c>/<c>0</c>.</summary>
    public static AdoptionOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("MEMORY_ADOPTION_HINTS");
        var disabled = string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0";
        return new AdoptionOptions(!disabled);
    }
}
