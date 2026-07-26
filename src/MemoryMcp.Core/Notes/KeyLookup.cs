namespace MemoryMcp.Core.Notes;

/// <summary>A note's stable address for a batch key lookup (MEMP-219).</summary>
/// <param name="Domain">The note's domain.</param>
/// <param name="Type">The note's type.</param>
/// <param name="DedupKey">The note's stable dedup key.</param>
public sealed record KeyRef(string Domain, string Type, string DedupKey);

/// <summary>One result of a batch key lookup (MEMP-219): the requested key plus an EXPLICIT found flag, so a
/// miss is unambiguous (never a bare null).</summary>
/// <param name="Domain">The requested domain.</param>
/// <param name="Type">The requested type.</param>
/// <param name="DedupKey">The requested dedup key.</param>
/// <param name="Found">True when a note resolved (and was in scope).</param>
/// <param name="Note">The resolved note view, or null when <paramref name="Found"/> is false.</param>
public sealed record NoteByKey(string Domain, string Type, string DedupKey, bool Found, NoteView? Note);

/// <summary>The next unused ticket key for a project (MEMP-220): a read-only peek, not a hard reservation.</summary>
/// <param name="Project">The project the key belongs to.</param>
/// <param name="Prefix">The key prefix (e.g. MEMP, TRD), upper-cased.</param>
/// <param name="NextKey">The suggested next key, e.g. <c>MEMP-229</c> (at least 3 digits).</param>
/// <param name="CurrentMax">The highest numeric suffix currently in use, or null if the project has none.</param>
/// <param name="MatchedKeys">How many existing keys matched the prefix.</param>
public sealed record NextKeyResult(string Project, string Prefix, string NextKey, int? CurrentMax, int MatchedKeys);
