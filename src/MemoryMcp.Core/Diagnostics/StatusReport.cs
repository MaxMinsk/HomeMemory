namespace MemoryMcp.Core.Diagnostics;

/// <summary>A snapshot of server/database health for the <c>status</c> tool.</summary>
/// <param name="SchemaVersion">The database's current schema version (PRAGMA user_version).</param>
/// <param name="RegisteredSchemas">Registered payload schemas as <c>type@version</c> strings.</param>
/// <param name="NoteCount">Number of live (non-deleted) notes.</param>
/// <param name="SearchBackend">A description of the search backend in use.</param>
public sealed record StatusReport(
    int SchemaVersion, IReadOnlyList<string> RegisteredSchemas, long NoteCount, string SearchBackend);
