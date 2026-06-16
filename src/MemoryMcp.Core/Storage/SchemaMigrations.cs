using MemoryMcp.Core.Storage.Migrations;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// The ordered set of production schema migrations applied to the memory database.
/// The host builds a <see cref="Migrator"/> from this list at startup (MEMP-009).
/// </summary>
public static class SchemaMigrations
{
    /// <summary>All production migrations, in application order.</summary>
    public static IReadOnlyList<IMigration> All { get; } = new IMigration[]
    {
        new Migration0001Init(),
        new Migration0002Fts(),
        new Migration0003PendingActions(),
        new Migration0004Attachments(),
        new Migration0005FtsDedupKey(),
        new Migration0006PendingActionDomain(),
        new Migration0007Tokens(),
        new Migration0008FtsPayload(),
        new Migration0009LinkUnique(),
        new Migration0010NoteUsage(),
    };
}
