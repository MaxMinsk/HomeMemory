using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>Canonical JSON wire form of a <see cref="NoteChangedEvent"/> — the body-free facets shared by every
/// push sink (MQTT topic payload, HTTP webhook body). Centralized so all transports agree on the shape.</summary>
public static class NoteEventJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes a change event to its canonical JSON (id/domain/type/project/tags/op/ts).</summary>
    /// <param name="e">The change event.</param>
    public static string Serialize(NoteChangedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return JsonSerializer.Serialize(
            new { id = e.Id, domain = e.Domain, type = e.Type, project = e.Project, tags = e.Tags, op = e.Op, ts = e.Ts },
            Options);
    }
}
