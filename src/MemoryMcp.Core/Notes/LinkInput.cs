namespace MemoryMcp.Core.Notes;

/// <summary>One edge of a bulk link (MEMP-181): a directed relation from one note to another.</summary>
/// <param name="FromId">Source note id.</param>
/// <param name="ToId">Target note id.</param>
/// <param name="Rel">Relationship verb (active-voice lower_snake_case).</param>
public sealed record LinkInput(string FromId, string ToId, string Rel);

/// <summary>The outcome of a bulk link: how many edges were newly created vs already present (MEMP-181).</summary>
/// <param name="Created">Newly created edges.</param>
/// <param name="AlreadyPresent">Edges that already existed (idempotent no-ops).</param>
public sealed record LinkManyResult(int Created, int AlreadyPresent);
