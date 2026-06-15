using MemoryMcp.Core.Backlog;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Backlog;

public class BacklogTests
{
    private const string Sample = @"# Backlog

## Current focus
Just prose, no items here.

## Done
- MEMP-001: scaffold done.
- MEMP-002: sqlite done.

## Ready
### MEMP-003: First ready task
Body line one.
**Acceptance:** something measurable.

## Next
### MEMP-004: A next task
Next body.

### From reference analysis
- MEMP-010: idea from analysis.

## Later
- MEMP-020: later one.
";

    [Fact]
    public void Parse_maps_sections_to_status()
    {
        var items = BacklogImporter.Parse(Sample);

        Assert.Equal(6, items.Count);
        Assert.Equal("done", Status(items, "MEMP-001"));
        Assert.Equal("ready", Status(items, "MEMP-003"));
        Assert.Equal("next", Status(items, "MEMP-004"));
        Assert.Equal("next", Status(items, "MEMP-010"));   // one-liner under a non-task ### subsection, still in Next
        Assert.Equal("later", Status(items, "MEMP-020"));
        Assert.Contains("Body line one", items.First(i => i.Key == "MEMP-003").Body);
    }

    [Fact]
    public void Import_is_idempotent_and_validates()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);

        var first = BacklogImporter.Import(repo, Sample);
        var second = BacklogImporter.Import(repo, Sample);

        Assert.Equal(6, first);
        Assert.Equal(6, second);
        Assert.Equal(6, repo.List("memory-mcp", "backlog_item").Count);
    }

    [Fact]
    public void Import_export_round_trips()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        BacklogImporter.Import(repo, Sample);

        var markdown = BacklogExporter.Export(repo);
        var reparsed = BacklogImporter.Parse(markdown);

        Assert.Equal(6, reparsed.Count);
        foreach (var original in BacklogImporter.Parse(Sample))
        {
            Assert.Equal(original.Status, Status(reparsed, original.Key));
        }
    }

    private static string Status(IReadOnlyList<BacklogItem> items, string key) =>
        items.First(item => item.Key == key).Status;

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }
}
