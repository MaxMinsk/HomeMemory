using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-245: <c>notes_patch</c> rejected a payload <c>notes_upsert</c> had just accepted. MEMP-154 lets any note
/// carry a top-level <c>project</c> and lifts it to the envelope, and MEMP-198 made that strip schema-aware — but
/// only on the upsert path. Patch re-validated the MERGED payload, which still carried the stored <c>project</c>,
/// so a strict-typed note (<c>additionalProperties: false</c> — reference, fact, decision, most of the corpus)
/// could not be patched at all, for any field. Hit in the field while editing a reference note.
/// </summary>
public class PatchProjectLiftTests
{
    [Fact]
    public void A_project_scoped_note_of_a_strict_type_can_be_patched()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // fact@1 forbids any field it does not declare, so `project` survives only via the lift.
        var created = repo.Upsert("development", "fact", "A source card", "body",
            """{ "project": "memory-mcp", "statement": "a dated claim" }""", null, "ref-1", "tester");

        var patched = repo.Patch(created.Id, "A better title", null, null, null, null, "tester")!;

        Assert.Equal("A better title", patched.Title);
        Assert.Equal("memory-mcp", patched.Project); // the envelope axis survives the patch
    }

    [Fact]
    public void A_type_that_declares_project_itself_still_validates_it()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        // project_state@1 REQUIRES project in its payload, so the strip must not apply to it (MEMP-198).
        var created = repo.Upsert("development", "project_state", "Rollout", "body",
            """{ "project": "memory-mcp", "state": "in progress" }""", null, "state-1", "tester");

        var patched = repo.Patch(created.Id, null, "a new body", null, null, null, "tester")!;

        Assert.Equal("a new body", patched.Body);
        Assert.Contains("memory-mcp", patched.PayloadJson!, StringComparison.Ordinal); // kept in the payload, not stripped
    }

    [Fact]
    public void A_patch_that_breaks_the_schema_is_still_rejected()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var created = repo.Upsert("development", "fact", "A source card", "body",
            """{ "project": "memory-mcp", "statement": "a dated claim" }""", null, "ref-1", "tester");

        // The lift must not turn into a blanket "accept anything": an undeclared field still fails.
        Assert.Throws<NoteValidationException>(() =>
            repo.Patch(created.Id, null, null, """{ "not_in_the_schema": "x" }""", null, null, "tester"));
    }

    [Fact]
    public void The_same_lift_applies_to_a_bulk_patch()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var first = repo.Upsert("development", "fact", "First card", "body",
            """{ "project": "memory-mcp", "statement": "one" }""", null, "ref-1", "tester");
        var second = repo.Upsert("development", "fact", "Second card", "body",
            """{ "project": "memory-mcp", "statement": "two" }""", null, "ref-2", "tester");

        var results = repo.PatchMany(
            [new PatchInput(first.Id, "First renamed", null, null, null, null),
             new PatchInput(second.Id, "Second renamed", null, null, null, null)],
            "tester");

        Assert.Equal(2, results.Count);
        Assert.Equal("First renamed", repo.Get(first.Id)!.Title);
        Assert.Equal("Second renamed", repo.Get(second.Id)!.Title);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
