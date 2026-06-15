using Json.Schema;

namespace MemoryMcp.Core.Schemas;

/// <summary>A registered payload schema for a note <c>type</c> at a specific version.</summary>
/// <param name="Type">The note type, e.g. <c>backlog_item</c>.</param>
/// <param name="Version">The schema version, parsed from the schema's <c>$id</c> (e.g. 1).</param>
/// <param name="Json">The raw JSON Schema document.</param>
/// <param name="Compiled">The compiled schema used for validation.</param>
public sealed record SchemaDefinition(string Type, int Version, string Json, JsonSchema Compiled);
