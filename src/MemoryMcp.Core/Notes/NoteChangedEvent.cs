namespace MemoryMcp.Core.Notes;

/// <summary>
/// A real-time, body-free description of a note write, published to interested sinks (e.g. MQTT for
/// Home Assistant). Carries only envelope facets — NEVER the body or any payload value — so it is safe
/// to fan out over a broker.
/// </summary>
/// <param name="Id">The note id.</param>
/// <param name="Domain">The note's domain (namespace).</param>
/// <param name="Type">The note's type (schema discriminator).</param>
/// <param name="Project">Optional project sub-axis within the domain; null when unset.</param>
/// <param name="Tags">The note's tags (empty when none).</param>
/// <param name="Op">The write operation: create, update, supersede, or archive.</param>
/// <param name="Ts">The UTC timestamp of the write (ISO 8601 round-trip).</param>
public sealed record NoteChangedEvent(
    string Id, string Domain, string Type, string? Project,
    IReadOnlyList<string> Tags, string Op, string Ts);
