namespace MemoryMcp.Core.Notes;

/// <summary>
/// The outcome of resolving a project name that was passed where a domain is expected (MEMP-212): the real
/// <see cref="Domain"/> that holds that project's notes, the <see cref="Project"/> that was matched, and how
/// many active notes back it. Lets memory_context/domain_manifest recover from the common project-vs-domain
/// mix-up (e.g. domain='unity-solitaire') with a corrective warning instead of an empty result.
/// </summary>
/// <param name="Domain">The domain the project actually lives in (the one holding the most of its notes).</param>
/// <param name="Project">The matched project (normalized/lowercased).</param>
/// <param name="NoteCount">Active notes in that domain carrying this project.</param>
public sealed record ProjectDomainResolution(string Domain, string Project, long NoteCount);
