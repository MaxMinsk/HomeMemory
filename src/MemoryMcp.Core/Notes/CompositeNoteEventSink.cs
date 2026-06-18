namespace MemoryMcp.Core.Notes;

/// <summary>
/// Fans a note-change event out to several sinks (e.g. MQTT and an HTTP webhook running together, MEMP-184).
/// Best-effort like every sink: one sink throwing never stops the others and never reaches the write path.
/// </summary>
public sealed class CompositeNoteEventSink : INoteEventSink
{
    private readonly IReadOnlyList<INoteEventSink> _sinks;

    /// <summary>Creates a composite over the given sinks (publishes to each, in order).</summary>
    /// <param name="sinks">The sinks to fan out to.</param>
    public CompositeNoteEventSink(IReadOnlyList<INoteEventSink> sinks) =>
        _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));

    /// <inheritdoc />
    public void Publish(NoteChangedEvent e)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Publish(e);
            }
            catch (Exception)
            {
                // A misbehaving sink must not block the others or the write path; each sink also guards itself.
            }
        }
    }
}
