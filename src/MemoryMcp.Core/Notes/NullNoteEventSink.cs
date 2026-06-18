namespace MemoryMcp.Core.Notes;

/// <summary>The default no-op sink: drops every event. Used when real-time publishing is disabled.</summary>
public sealed class NullNoteEventSink : INoteEventSink
{
    /// <summary>A shared instance (the sink is stateless).</summary>
    public static readonly NullNoteEventSink Instance = new();

    /// <inheritdoc />
    public void Publish(NoteChangedEvent e)
    {
        // intentionally does nothing
    }
}
