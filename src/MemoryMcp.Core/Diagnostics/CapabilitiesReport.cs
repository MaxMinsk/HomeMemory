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
public sealed record CapabilitiesReport(
    string ServerVersion, int SchemaVersion, int ContractVersion,
    IReadOnlyList<NoteTypeInfo> Types, ScopeInfo Scope, string SearchBackend,
    long BlobQuotaBytes, string CommonsDomain, string SkillsHint);

/// <summary>A note type the server recognizes.</summary>
/// <param name="Type">The type discriminator (e.g. <c>backlog_item</c>, <c>recipe</c>).</param>
/// <param name="LatestVersion">The highest registered schema version for this type.</param>
/// <param name="Builtin">True when the type ships with the server; false when agent-authored via schema_upsert.</param>
public sealed record NoteTypeInfo(string Type, int LatestVersion, bool Builtin);

/// <summary>The caller's domain access, derived from the bearer token.</summary>
/// <param name="Unrestricted">True for a trusted scope with access to every domain (e.g. local stdio / root token).</param>
/// <param name="ReadableDomains">Domains the caller may read (includes commons); empty when unrestricted.</param>
/// <param name="WritableDomains">Domains the caller may write (excludes commons); empty when unrestricted.</param>
/// <param name="CommonsReadable">Always true: the commons domain is world-readable.</param>
public sealed record ScopeInfo(
    bool Unrestricted, IReadOnlyList<string> ReadableDomains,
    IReadOnlyList<string> WritableDomains, bool CommonsReadable);
