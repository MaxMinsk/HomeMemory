using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// Regression: notes_patch / notes_assemble must ECHO the envelope project, not return null. The DB always
// preserved it (the patch UPDATE never touched the column); only the return value was missing it, which scared
// a consumer into thinking patch had dropped notes out of project scope.
public class NotesProjectEchoTests
{
    private const string ProjectPayload = """{ "key": "MEMP-201", "status": "ready", "project": "memory-mcp" }""";

    [Fact]
    public void Patch_returns_and_preserves_the_envelope_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var created = repo.Upsert("development", "backlog_item", "Title", "body", ProjectPayload, null, "MEMP-201", "t");
        Assert.Equal("memory-mcp", repo.Get(created.Id)!.Project); // sanity: scope set at upsert

        var patched = repo.Patch(created.Id, null, "new body", null, null, null, "t");

        Assert.NotNull(patched);
        Assert.Equal("memory-mcp", patched!.Project);             // the return now carries the project (was null)
        Assert.Equal("memory-mcp", repo.Get(created.Id)!.Project); // and the DB still has it (never lost)
    }

    [Fact]
    public void Assemble_create_returns_the_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var result = repo.Assemble("development", "backlog_item", "Title", "body", ProjectPayload, null, "MEMP-201", null, "t");

        Assert.Equal("memory-mcp", result.Project);
    }

    [Fact]
    public void Assemble_dedup_update_without_payload_project_still_echoes_the_preserved_project()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        repo.Upsert("development", "backlog_item", "Title", "body", ProjectPayload, null, "MEMP-201", "t");

        // Re-assemble the same key with a payload that omits project: the stored project must be preserved + echoed.
        var result = repo.Assemble("development", "backlog_item", "Title", "changed", """{ "key": "MEMP-201", "status": "next" }""", null, "MEMP-201", null, "t");

        Assert.False(result.Created);
        Assert.Equal("memory-mcp", result.Project);
        Assert.Equal("memory-mcp", repo.Get(result.Id)!.Project);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
