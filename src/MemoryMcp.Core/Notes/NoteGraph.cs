namespace MemoryMcp.Core.Notes;

/// <summary>A node in a note's link-graph neighborhood: the note plus its hop distance from the root.</summary>
/// <param name="Id">The note id.</param>
/// <param name="Title">The note title, if any.</param>
/// <param name="Type">The note type.</param>
/// <param name="Domain">The note domain.</param>
/// <param name="Hops">Distance from the root note (0 = the root itself).</param>
public sealed record GraphNode(string Id, string? Title, string Type, string Domain, int Hops);

/// <summary>A directed edge in the link graph, in its real from→to direction with the relation verb.</summary>
/// <param name="FromId">The source note id.</param>
/// <param name="ToId">The target note id.</param>
/// <param name="Rel">The relationship verb (e.g. <c>depends_on</c>, <c>in_sprint</c>).</param>
public sealed record GraphEdge(string FromId, string ToId, string Rel);

/// <summary>
/// The N-hop link-graph neighborhood of a root note (MEMP-031): the reachable nodes (with hop distance) and the
/// edges between them, all scope-filtered. <see cref="Truncated"/> is true when the node cap stopped the walk.
/// </summary>
/// <param name="RootId">The note the traversal started from.</param>
/// <param name="Nodes">Reachable nodes including the root (hop 0).</param>
/// <param name="Edges">Edges among the returned nodes (deduplicated).</param>
/// <param name="Truncated">True when the node cap halted expansion before the graph was exhausted.</param>
public sealed record NoteGraph(string RootId, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool Truncated);
