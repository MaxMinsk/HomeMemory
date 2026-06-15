using System.Reflection;
using MemoryMcp.Tests.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MemoryMcp.Tests.Integration;

public class StdioRoundTripTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Stdio_client_lists_and_calls_tools_end_to_end()
    {
        using var temp = new TempDatabase();
        var serverDll = LocateServerDll();
        Assert.True(File.Exists(serverDll), $"Server build not found: {serverDll}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "memory-test",
            Command = "dotnet",
            Arguments = new[] { serverDll },
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["MEMORY_TRANSPORT"] = "stdio",
                ["MEMORY_DB_PATH"] = temp.FilePath,
            },
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);

        // The server forces cross-domain authoring guidance via initialize instructions.
        Assert.Contains("memory-authoring", client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);

        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Contains(tools, tool => tool.Name == "notes_upsert");
        Assert.Contains(tools, tool => tool.Name == "notes_search");
        Assert.Contains(tools, tool => tool.Name == "status");
        Assert.Contains(tools, tool => tool.Name == "skill_upsert");
        Assert.Contains(tools, tool => tool.Name == "skill_get");
        Assert.Contains(tools, tool => tool.Name == "notes_confirm");
        Assert.Contains(tools, tool => tool.Name == "artifacts_put");
        Assert.Contains(tools, tool => tool.Name == "artifacts_get");
        Assert.Contains(tools, tool => tool.Name == "schema_upsert");
        Assert.Contains(tools, tool => tool.Name == "artifacts_delete");

        await AssertNotesRoundTrip(client, cts.Token);
        await AssertSkillRoundTrip(client, cts.Token);
        await AssertArtifactRoundTrip(client, cts.Token);
        await AssertValidationErrorCarriesSkillHint(client, cts.Token);
    }

    private static async Task AssertArtifactRoundTrip(McpClient client, CancellationToken ct)
    {
        // Inline content path (agent-generated text) — must not require bytes through the model context.
        var put = await client.CallToolAsync("artifacts_put", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["content"] = "# rendered demo", ["filename"] = "demo.md", ["contentType"] = "text/markdown",
        }, cancellationToken: ct);
        Assert.True(put.IsError is not true, "artifacts_put reported an error");
        Assert.Contains("blob://", Text(put));
    }

    private static async Task AssertNotesRoundTrip(McpClient client, CancellationToken ct)
    {
        var upsert = await client.CallToolAsync("notes_upsert", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["type"] = "backlog_item", ["title"] = "Round-trip demo",
            ["payload"] = "{ \"key\": \"MEMP-900\", \"status\": \"ready\" }", ["dedupKey"] = "MEMP-900",
        }, cancellationToken: ct);
        Assert.True(upsert.IsError is not true, "notes_upsert reported an error");

        var search = await client.CallToolAsync("notes_search",
            new Dictionary<string, object?> { ["domain"] = "memory-mcp" }, cancellationToken: ct);
        Assert.True(search.IsError is not true, "notes_search reported an error");
        Assert.Contains("Round-trip demo", Text(search));
    }

    private static async Task AssertSkillRoundTrip(McpClient client, CancellationToken ct)
    {
        var upsert = await client.CallToolAsync("skill_upsert", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["key"] = "backlog-mgmt", ["title"] = "Backlog management",
            ["body"] = "Use MEMP-NNN keys; statuses ready/next/later/in_progress/done.", ["targetType"] = "backlog_item",
        }, cancellationToken: ct);
        Assert.True(upsert.IsError is not true, "skill_upsert reported an error");

        var get = await client.CallToolAsync("skill_get",
            new Dictionary<string, object?> { ["domain"] = "memory-mcp", ["key"] = "backlog-mgmt" }, cancellationToken: ct);
        Assert.True(get.IsError is not true, "skill_get reported an error");
        Assert.Contains("MEMP-NNN", Text(get));
    }

    private static async Task AssertValidationErrorCarriesSkillHint(McpClient client, CancellationToken ct)
    {
        // Invalid payload surfaces as a readable tool error; a skill guides backlog_item, so it carries the hint.
        var invalid = await client.CallToolAsync("notes_upsert", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["type"] = "backlog_item", ["payload"] = "{ \"status\": \"bogus\" }",
        }, cancellationToken: ct);
        Assert.True(invalid.IsError is true, "invalid payload should surface as a tool error");
        var error = Text(invalid);
        Assert.Contains("schema validation", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backlog-mgmt", error, StringComparison.Ordinal); // skill hint
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static string LocateServerDll()
    {
        // Run-time override (CI flexibility), else the path embedded at build time (see the test .csproj).
        var fromEnv = Environment.GetEnvironmentVariable("MEMORY_SERVER_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var embedded = typeof(StdioRoundTripTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ServerDll")?.Value;
        return embedded is not null ? Path.GetFullPath(embedded) : throw new InvalidOperationException(
            "Server dll path not embedded; set MEMORY_SERVER_DLL or rebuild the test project.");
    }
}
