using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Confirmation;

public class ConfirmationServiceTests
{
    private const string Payload = """{ "key": "MEMP-100", "status": "ready" }""";

    [Fact]
    public void Request_then_confirm_archives_the_note()
    {
        using var temp = new TempDatabase();
        var (confirm, notes) = NewServices(temp);
        var id = notes.Upsert("memory-mcp", "backlog_item", "T", null, Payload, null, "MEMP-100", "t").Id;

        var pending = confirm.Request("archive", id, null, null, "tester");
        Assert.Equal("pending", pending.Status);
        Assert.Equal("active", notes.Get(id)!.Status); // not yet archived

        var result = confirm.Confirm(pending.Token, "tester");
        Assert.True(result.Executed);
        Assert.Equal("archived", notes.Get(id)!.Status);
    }

    [Fact]
    public void Confirm_executes_at_most_once()
    {
        using var temp = new TempDatabase();
        var (confirm, notes) = NewServices(temp);
        var id = notes.Upsert("memory-mcp", "backlog_item", "T", null, Payload, null, "MEMP-100", "t").Id;
        var pending = confirm.Request("archive", id, null, null, "t");

        confirm.Confirm(pending.Token, "t");
        var second = Assert.Throws<ConfirmationException>(() => confirm.Confirm(pending.Token, "t"));
        Assert.Contains("already executed", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancel_prevents_execution()
    {
        using var temp = new TempDatabase();
        var (confirm, notes) = NewServices(temp);
        var id = notes.Upsert("memory-mcp", "backlog_item", "T", null, Payload, null, "MEMP-100", "t").Id;
        var pending = confirm.Request("archive", id, null, null, "t");

        confirm.Cancel(pending.Token, "t");
        Assert.Throws<ConfirmationException>(() => confirm.Confirm(pending.Token, "t"));
        Assert.Equal("active", notes.Get(id)!.Status); // never archived
    }

    [Fact]
    public void Confirm_supersede_marks_old_superseded()
    {
        using var temp = new TempDatabase();
        var (confirm, notes) = NewServices(temp);
        var oldId = notes.Upsert("memory-mcp", "backlog_item", "A", null, Payload, null, "MEMP-100", "t").Id;
        var newId = notes.Upsert("memory-mcp", "backlog_item", "B", null, """{ "key": "MEMP-101", "status": "ready" }""", null, "MEMP-101", "t").Id;

        var pending = confirm.Request("supersede", oldId, newId, null, "t");
        confirm.Confirm(pending.Token, "t");

        Assert.Equal("superseded", notes.Get(oldId)!.Status);
    }

    [Fact]
    public void Confirm_unknown_token_throws()
    {
        using var temp = new TempDatabase();
        var (confirm, _) = NewServices(temp);
        var ex = Assert.Throws<ConfirmationException>(() => confirm.Confirm("nope", "t"));
        Assert.Contains("Unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("explode")]   // unsupported action
    public void Request_rejects_unknown_action(string action)
    {
        using var temp = new TempDatabase();
        var (confirm, notes) = NewServices(temp);
        var id = notes.Upsert("memory-mcp", "backlog_item", "T", null, Payload, null, "MEMP-100", "t").Id;
        Assert.Throws<ConfirmationException>(() => confirm.Request(action, id, null, null, "t"));
    }

    [Fact]
    public void Request_rejects_missing_target()
    {
        using var temp = new TempDatabase();
        var (confirm, _) = NewServices(temp);
        Assert.Throws<ConfirmationException>(() => confirm.Request("archive", "ghost", null, null, "t"));
    }

    private static (ConfirmationService Confirm, NotesRepository Notes) NewServices(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var notes = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        return (new ConfirmationService(factory, notes), notes);
    }
}
