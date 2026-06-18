namespace MemoryMcp.Core.Notes;

/// <summary>
/// Receives note-change notifications emitted after a write commits. Implementations MUST be best-effort:
/// <see cref="Publish"/> is called from the write path and must never throw or block it meaningfully.
/// </summary>
public interface INoteEventSink
{
    /// <summary>Publishes a note-change event. Best-effort: implementations must swallow their own failures.</summary>
    /// <param name="e">The change event (envelope facets only; no body/payload).</param>
    void Publish(NoteChangedEvent e);
}
