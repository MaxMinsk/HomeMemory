using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-198: the envelope-axis `project` lift (MEMP-154) must NOT strip `project` from types whose schema declares
// it (e.g. project_state, where it is required) — that previously failed those writes as "missing required project".
public class NotesProjectSchemaTests
{
    private const string ProjectState = """{ "project": "mining-rush", "state": "v1 in prod" }""";

    [Fact]
    public void Project_state_validates_and_keeps_its_project_field()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var result = repo.Upsert("development", "project_state", "Mining state", null, ProjectState, null, "ps-mining", "t");

        Assert.True(result.Created); // no "required property project missing" rejection
        var note = repo.Get(result.Id)!;
        Assert.Equal("mining-rush", note.Project);          // envelope axis derived from payload.project
        Assert.Contains("\"project\"", note.PayloadJson!, System.StringComparison.Ordinal); // and the schema field is kept in the payload
    }

    [Fact]
    public void Batch_upsert_accepts_project_state()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var results = repo.UpsertMany(
            new[] { new NoteUpsertInput("development", "project_state", "S", null, ProjectState, null, "ps-1") }, "t");

        Assert.True(results[0].Created); // batch validation no longer strips the required project
    }

    [Fact]
    public void Backlog_item_still_lifts_payload_project_to_the_envelope()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        // backlog_item@1 is additionalProperties:false with no `project` property — the lift must still apply.
        var result = repo.Upsert("development", "backlog_item", "T", null,
            """{ "key": "MEMP-201", "status": "ready", "project": "memory-mcp" }""", null, "MEMP-201", "t");

        Assert.True(result.Created);
        Assert.Equal("memory-mcp", repo.Get(result.Id)!.Project);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
