using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesPartialReadTests
{
    [Fact]
    public void ReadBody_returns_window_with_continuation_contract()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var id = Seed(repo, body: "0123456789"); // 10 chars

        var first = repo.ReadBody(id, offset: 0, limitChars: 4);
        Assert.NotNull(first);
        Assert.Equal("0123", first!.Content);
        Assert.Equal(0, first.Offset);
        Assert.Equal(4, first.ReturnedChars);
        Assert.Equal(10, first.TotalChars);
        Assert.True(first.Truncated);
        Assert.Equal(4, first.NextOffset);

        var rest = repo.ReadBody(id, offset: first.NextOffset!.Value, limitChars: 100);
        Assert.Equal("456789", rest!.Content);
        Assert.False(rest.Truncated);
        Assert.Null(rest.NextOffset);
    }

    [Fact]
    public void ReadBody_clamps_offset_past_end_to_empty_tail()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var id = Seed(repo, body: "abc");

        var slice = repo.ReadBody(id, offset: 999, limitChars: 10);
        Assert.Equal(string.Empty, slice!.Content);
        Assert.Equal(3, slice.Offset);     // clamped to total
        Assert.Equal(3, slice.TotalChars);
        Assert.False(slice.Truncated);
        Assert.Null(slice.NextOffset);
    }

    [Fact]
    public void ReadBody_handles_empty_body_and_missing_note()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var id = Seed(repo, body: null);

        var slice = repo.ReadBody(id);
        Assert.Equal(string.Empty, slice!.Content);
        Assert.Equal(0, slice.TotalChars);
        Assert.False(slice.Truncated);

        Assert.Null(repo.ReadBody("nope"));
    }

    [Fact]
    public void Outline_maps_headings_to_offsets_usable_by_ReadBody()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var id = Seed(repo, body: "# A\nintro\n## B\nmore");

        var outline = repo.Outline(id);
        Assert.NotNull(outline);
        Assert.Equal(2, outline!.Headings.Count);
        Assert.Equal(1, outline.Headings[0].Level);
        Assert.Equal("A", outline.Headings[0].Text);
        Assert.Equal(0, outline.Headings[0].Offset);
        Assert.Equal(2, outline.Headings[1].Level);
        Assert.Equal("B", outline.Headings[1].Text);

        // The offset addresses the heading line: a section read starts exactly there.
        var heading = repo.ReadBody(id, outline.Headings[1].Offset, 4);
        Assert.Equal("## B", heading!.Content);
    }

    [Fact]
    public void Outline_ignores_headings_inside_code_fences()
    {
        using var temp = new TempDatabase();
        var repo = NewRepo(temp);
        var id = Seed(repo, body: "```\n# not a heading\n```\n# real");

        var outline = repo.Outline(id);
        var heading = Assert.Single(outline!.Headings);
        Assert.Equal("real", heading.Text);
    }

    private static NotesRepository NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
    }

    private static string Seed(NotesRepository repo, string? body) =>
        repo.Upsert("memory-mcp", "backlog_item", "T", body,
            """{ "key": "MEMP-200", "status": "ready" }""", null, "MEMP-200", "tester").Id;
}
