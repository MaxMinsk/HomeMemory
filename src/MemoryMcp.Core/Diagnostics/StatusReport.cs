namespace MemoryMcp.Core.Diagnostics;

/// <summary>A snapshot of server/database health and contents for the <c>status</c> tool.</summary>
/// <param name="SchemaVersion">The database's current schema version (PRAGMA user_version).</param>
/// <param name="RegisteredSchemas">Registered payload schemas as <c>type@version</c> strings.</param>
/// <param name="NoteCount">Number of live (non-deleted) notes.</param>
/// <param name="NotesByType">Live note counts grouped by type (e.g. backlog_item, recipe, skill, sprint).</param>
/// <param name="NotesByDomain">Live note counts grouped by domain (namespace), for discoverability.</param>
/// <param name="NotesByStatus">Note counts grouped by envelope status (active / archived / superseded).</param>
/// <param name="AttachmentCount">Number of attachment rows.</param>
/// <param name="BlobBytes">Total bytes stored in the content-addressed blob store.</param>
/// <param name="PendingActionsCount">Number of unresolved destructive-action confirmations awaiting confirm/cancel.</param>
/// <param name="SearchBackend">A description of the search backend in use.</param>
/// <param name="ServerVersion">The running server build version (matches the add-on release).</param>
/// <param name="BlobQuotaBytes">The configured blob-store byte quota (0 = unlimited).</param>
/// <param name="DbSizeBytes">On-disk database size in bytes: the main file plus the WAL/SHM sidecars when present.</param>
public sealed record StatusReport(
    int SchemaVersion, IReadOnlyList<string> RegisteredSchemas, long NoteCount,
    IReadOnlyDictionary<string, long> NotesByType, IReadOnlyDictionary<string, long> NotesByDomain,
    IReadOnlyDictionary<string, long> NotesByStatus, long AttachmentCount, long BlobBytes,
    long PendingActionsCount, string SearchBackend, string ServerVersion, long BlobQuotaBytes,
    long DbSizeBytes);
