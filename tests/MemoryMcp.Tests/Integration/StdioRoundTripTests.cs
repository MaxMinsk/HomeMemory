using MemoryMcp.Tests.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MemoryMcp.Tests.Integration;

public class StdioRoundTripTests
{
    [Fact]
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

        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Contains(tools, tool => tool.Name == "notes_upsert");
        Assert.Contains(tools, tool => tool.Name == "notes_search");
        Assert.Contains(tools, tool => tool.Name == "status");

        var upsert = await client.CallToolAsync(
            "notes_upsert",
            new Dictionary<string, object?>
            {
                ["domain"] = "memory-mcp",
                ["type"] = "backlog_item",
                ["title"] = "Round-trip demo",
                ["payload"] = "{ \"key\": \"MEMP-900\", \"status\": \"ready\" }",
                ["dedupKey"] = "MEMP-900",
            },
            cancellationToken: cts.Token);
        Assert.True(upsert.IsError is not true, "notes_upsert reported an error");

        var search = await client.CallToolAsync(
            "notes_search",
            new Dictionary<string, object?> { ["domain"] = "memory-mcp" },
            cancellationToken: cts.Token);
        Assert.True(search.IsError is not true, "notes_search reported an error");

        var text = string.Concat(search.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("Round-trip demo", text);

        // An invalid payload must surface to the model as a readable error (McpException), not a silent generic failure.
        var invalid = await client.CallToolAsync(
            "notes_upsert",
            new Dictionary<string, object?>
            {
                ["domain"] = "memory-mcp",
                ["type"] = "backlog_item",
                ["payload"] = "{ \"status\": \"bogus\" }",
            },
            cancellationToken: cts.Token);
        Assert.True(invalid.IsError is true, "invalid payload should surface as a tool error");
        var error = string.Concat(invalid.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("schema validation", error, StringComparison.OrdinalIgnoreCase);
    }

    private static string LocateServerDll()
    {
        // .../tests/MemoryMcp.Tests/bin/<cfg>/net8.0  ->  repo root, then into the server's build output.
        var net = Path.GetDirectoryName(typeof(StdioRoundTripTests).Assembly.Location)!;
        var configDir = Path.GetDirectoryName(net)!;            // .../bin/<cfg>
        var configuration = Path.GetFileName(configDir);        // Release / Debug
        var bin = Path.GetDirectoryName(configDir)!;            // .../bin
        var projectDir = Path.GetDirectoryName(bin)!;           // .../tests/MemoryMcp.Tests
        var testsDir = Path.GetDirectoryName(projectDir)!;      // .../tests
        var repo = Path.GetDirectoryName(testsDir)!;            // repo root
        return Path.Combine(repo, "src", "MemoryMcp.Server", "bin", configuration, "net8.0", "MemoryMcp.Server.dll");
    }
}
