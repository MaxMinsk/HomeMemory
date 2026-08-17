namespace MemoryMcp.Core.Diagnostics;

/// <summary>The runtime contract returned by the <c>memory_capabilities</c> tool: what this build
/// supports and what the caller's token may reach, so an agent can discover capabilities on connect
/// rather than guessing from a tool list cached at an older session.</summary>
/// <param name="ServerVersion">The running server build version (matches the add-on release).</param>
/// <param name="SchemaVersion">The database's current schema version (PRAGMA user_version).</param>
/// <param name="ContractVersion">A small integer bumped when this contract changes meaningfully.</param>
/// <param name="Types">The note types this build knows, each with its latest schema version and built-in flag.</param>
/// <param name="Scope">The caller's domain scope (readable/writable domains plus commons).</param>
/// <param name="SearchBackend">A description of the search backend in use.</param>
/// <param name="BlobQuotaBytes">The configured blob-store byte quota (0 = unlimited).</param>
/// <param name="CommonsDomain">The world-readable shared domain holding core rules/skills.</param>
/// <param name="SkillsHint">A one-line pointer to the skills an agent should read before authoring.</param>
/// <param name="Retrieval">How note text is currently turned into indexable content (MEMP-251).</param>
public sealed record CapabilitiesReport(
    string ServerVersion, int SchemaVersion, int ContractVersion,
    IReadOnlyList<NoteTypeInfo> Types, ScopeInfo Scope, string SearchBackend,
    long BlobQuotaBytes, string CommonsDomain, string SkillsHint, RetrievalInfo Retrieval);

/// <summary>
/// The state of the retrieval mapping layer (MEMP-251): which mapping is in force, and how much of the type
/// surface still relies on the legacy "index every string" fallback rather than declaring what matters.
/// <para>Coverage is reported rather than assumed because it is the honest measure of the migration: a server
/// where every type is legacy behaves exactly as it did before the seam existed.</para>
/// </summary>
/// <param name="MappingVersion">Identity of the mapping in force.</param>
/// <param name="MappingHash">Stable hash of that mapping; a change means stored passages are stale.</param>
/// <param name="TypesWithMapping">Types with a declared retrieval mapping.</param>
/// <param name="TypesOnLegacy">Types still indexing every string because they declare none.</param>
/// <param name="VectorModel">The embedding model in use, or null when the layer is off (MEMP-196). Null means
/// recall is purely lexical — which an agent needs to know, or it cannot tell why the same paraphrased query
/// behaves differently on two instances.</param>
/// <param name="IndexedNotes">Notes with at least one passage under the current model and mapping.</param>
/// <param name="StalePassages">Passages left over from an older model or mapping, awaiting reindex.</param>
public sealed record RetrievalInfo(
    string MappingVersion, string MappingHash, int TypesWithMapping, int TypesOnLegacy,
    string? VectorModel = null, long IndexedNotes = 0, long StalePassages = 0);

/// <summary>A note type the server recognizes.</summary>
/// <param name="Type">The type discriminator (e.g. <c>backlog_item</c>, <c>recipe</c>).</param>
/// <param name="LatestVersion">The highest registered schema version for this type.</param>
/// <param name="Builtin">True when the type ships with the server; false when agent-authored via schema_upsert.</param>
public sealed record NoteTypeInfo(string Type, int LatestVersion, bool Builtin);

/// <summary>The caller's domain access, derived from the bearer token.</summary>
/// <param name="Unrestricted">True for a trusted scope with access to every domain (e.g. local stdio / root token).
/// Read the two domain lists through this flag: when it is false they are the LIMIT of what the token may reach;
/// when it is true they are an INVENTORY of the domains that currently exist, and the caller may also write to a
/// domain that does not exist yet (MEMP-236).</param>
/// <param name="ReadableDomains">Domains the caller may read (includes commons), or every existing domain when unrestricted.</param>
/// <param name="WritableDomains">Domains the caller may write (excludes commons), or every existing domain when unrestricted.</param>
/// <param name="CommonsReadable">Always true: the commons domain is world-readable.</param>
public sealed record ScopeInfo(
    bool Unrestricted, IReadOnlyList<string> ReadableDomains,
    IReadOnlyList<string> WritableDomains, bool CommonsReadable);
