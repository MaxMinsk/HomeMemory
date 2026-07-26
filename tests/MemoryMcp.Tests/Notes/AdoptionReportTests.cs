using System.Linq;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-207: per-agent adoption report — reads (agent_reads) vs writes (note_events.actor).
public class AdoptionReportTests
{
    [Fact]
    public void Reports_reads_writes_and_flags_writes_without_reading()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        var reads = new AgentReadStore(factory);

        // 'reader-agent' recalls then writes; 'writer-agent' only writes (no reads).
        reads.Record("reader-agent");
        reads.Record("reader-agent");
        var noteId = repo.Upsert("kitchen", "fact", "By reader", "b", """{ "statement": "x" }""", null, "r1", "reader-agent").Id;
        repo.Patch(noteId, null, null, """{ "statement": "y" }""", null, null, "reader-agent");
        repo.Upsert("kitchen", "fact", "By writer", "b", """{ "statement": "z" }""", null, "w1", "writer-agent");

        var report = repo.Adoption(null);

        var reader = Assert.Single(report.Agents, a => a.Agent == "reader-agent");
        Assert.Equal(2, reader.Reads);
        Assert.Equal(1, reader.Creates);
        Assert.Equal(1, reader.Patches);
        Assert.False(reader.WritesWithoutReading);

        var writer = Assert.Single(report.Agents, a => a.Agent == "writer-agent");
        Assert.Equal(0, writer.Reads);
        Assert.Equal(1, writer.Creates);
        Assert.True(writer.WritesWithoutReading); // wrote, never read

        Assert.Equal(2, report.TotalReads);
    }

    [Fact]
    public void Agents_are_ordered_by_writes_descending()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());

        repo.Upsert("kitchen", "fact", "one", "b", """{ "statement": "1" }""", null, "one", "busy");
        repo.Upsert("kitchen", "fact", "two", "b", """{ "statement": "2" }""", null, "two", "busy");
        repo.Upsert("kitchen", "fact", "three", "b", """{ "statement": "3" }""", null, "three", "quiet");

        var report = repo.Adoption(null);

        Assert.Equal("busy", report.Agents[0].Agent); // 2 writes ranks above 1
    }

    [Fact]
    public void Reports_which_projects_each_agent_wrote_to()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());

        // 'dev' writes twice to project memory-mcp and once to unity-solitaire; a kitchen note has no project.
        repo.Upsert("development", "fact", "a", "b", """{ "statement": "1" }""", null, "a", "dev", "memory-mcp");
        repo.Upsert("development", "fact", "b", "b", """{ "statement": "2" }""", null, "b", "dev", "memory-mcp");
        repo.Upsert("development", "fact", "c", "b", """{ "statement": "3" }""", null, "c", "dev", "unity-solitaire");
        repo.Upsert("kitchen", "fact", "k", "b", """{ "statement": "4" }""", null, "k", "dev");

        var dev = Assert.Single(repo.Adoption(null).Agents, a => a.Agent == "dev");

        Assert.NotNull(dev.Projects);
        Assert.Equal("memory-mcp", dev.Projects![0].Name);   // heaviest workspace first
        Assert.Equal(2, dev.Projects[0].Writes);
        Assert.Contains(dev.Projects, p => p.Name == "unity-solitaire" && p.Writes == 1);
        Assert.Contains(dev.Projects, p => p.Name == "kitchen" && p.Writes == 1); // no project -> domain shown
    }
}
