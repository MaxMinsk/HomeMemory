using System.Reflection;
using MemoryMcp.Tests.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MemoryMcp.Tests.Integration;

public class StdioRoundTripTests
{
    // Every tool the server must advertise over stdio (a missing one means a wiring regression).
    private static readonly string[] ExpectedTools =
    {
        "notes_upsert", "notes_search", "status", "skill_upsert", "skill_get", "notes_confirm",
        "artifacts_put", "artifacts_get", "schema_upsert", "artifacts_delete", "artifacts_url",
        "notes_patch", "notes_links", "notes_assemble", "notes_recall", "notes_recent", "notes_related",
        "notes_graph", "notes_suggest_capture", "notes_get_by_key", "artifacts_find_text",
        "memory_capabilities", "schema_provenance", "domain_manifest", "memory_context", "skill_consolidate_plan",
        "notes_changes", "notes_tags",
    };

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
        foreach (var name in ExpectedTools)
        {
            Assert.Contains(tools, tool => tool.Name == name);
        }

        await AssertCapabilities(client, cts.Token);
        await AssertNotesRoundTrip(client, cts.Token);
        await AssertStructuredInputs(client, cts.Token);
        await AssertReturnErgonomics(client, cts.Token);
        await AssertAssembleIsAtomic(client, cts.Token);
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

    private static async Task AssertCapabilities(McpClient client, CancellationToken ct)
    {
        // The runtime contract advertises the known types + the (stdio = unrestricted) scope.
        var caps = await client.CallToolAsync("memory_capabilities", new Dictionary<string, object?>(), cancellationToken: ct);
        Assert.True(caps.IsError is not true, "memory_capabilities reported an error: " + Text(caps));
        var text = Text(caps);
        Assert.Contains("backlog_item", text); // a known built-in type is listed
        Assert.Contains("commons", text);      // commons domain surfaced
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

        var recall = await client.CallToolAsync("notes_recall",
            new Dictionary<string, object?> { ["query"] = "Round-trip", ["domain"] = "memory-mcp" }, cancellationToken: ct);
        Assert.True(recall.IsError is not true, "notes_recall reported an error");
        Assert.Contains("Round-trip demo", Text(recall));
    }

    private static async Task AssertStructuredInputs(McpClient client, CancellationToken ct)
    {
        // MEMP-072: payload as a JSON object and tags as a JSON array (not double-serialized strings).
        var upsert = await client.CallToolAsync("notes_upsert", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["type"] = "backlog_item", ["title"] = "Structured demo",
            ["payload"] = new Dictionary<string, object?> { ["key"] = "MEMP-901", ["status"] = "ready" },
            ["tags"] = new[] { "from-test", "structured" },
            ["dedupKey"] = "MEMP-901",
        }, cancellationToken: ct);
        Assert.True(upsert.IsError is not true, "structured notes_upsert reported an error: " + Text(upsert));

        var search = await client.CallToolAsync("notes_search", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["query"] = "901", ["includePayload"] = true,
        }, cancellationToken: ct);
        var text = Text(search);
        Assert.Contains("MEMP-901", text);     // object payload was stored + indexed
        Assert.Contains("structured", text);   // array tags were stored
    }

    private static async Task AssertReturnErgonomics(McpClient client, CancellationToken ct)
    {
        // MEMP-109a: append_journal returns the created note's envelope (id + assigned dedupKey + typed tags),
        // so the caller needn't follow up with a get.
        var journal = await client.CallToolAsync("notes_append_journal", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["text"] = "Captured thought about caching", ["tags"] = "[\"idea\"]",
        }, cancellationToken: ct);
        Assert.True(journal.IsError is not true, "notes_append_journal reported an error: " + Text(journal));
        var envelope = Text(journal);
        Assert.Contains("journal-", envelope);        // the assigned dedupKey is echoed
        Assert.Contains("unstructured", envelope);    // typed tags include the auto-added marker

        // MEMP-109b: read responses carry the payload/tags already parsed (typed), not just JSON strings.
        var get = await client.CallToolAsync("notes_get_by_key", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["type"] = "backlog_item", ["dedupKey"] = "MEMP-901",
        }, cancellationToken: ct);
        Assert.True(get.IsError is not true, "notes_get_by_key reported an error: " + Text(get));
        var view = Text(get);
        Assert.Contains("\"payload\"", view);         // typed payload object (alongside payloadJson)
        Assert.Contains("\"tags\"", view);            // typed tags array (alongside tagsJson)
        Assert.Contains("from-test", view);           // a tag value from the structured upsert
    }

    private static async Task AssertAssembleIsAtomic(McpClient client, CancellationToken ct)
    {
        // MEMP-075: links bind as an array of objects; a missing target rolls back the whole assemble.
        var bad = await client.CallToolAsync("notes_assemble", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["type"] = "backlog_item",
            ["payload"] = new Dictionary<string, object?> { ["key"] = "MEMP-902", ["status"] = "ready" },
            ["dedupKey"] = "MEMP-902",
            ["links"] = new[] { new Dictionary<string, object?> { ["toId"] = "ghost", ["rel"] = "depends_on" } },
        }, cancellationToken: ct);
        Assert.True(bad.IsError is true, "assemble with a missing link target should error");

        var search = await client.CallToolAsync("notes_search",
            new Dictionary<string, object?> { ["domain"] = "memory-mcp", ["query"] = "902" }, cancellationToken: ct);
        Assert.DoesNotContain("MEMP-902", Text(search)); // rolled back — the note was not created

        // No links: the note is created normally.
        var ok = await client.CallToolAsync("notes_assemble", new Dictionary<string, object?>
        {
            ["domain"] = "memory-mcp", ["type"] = "backlog_item", ["title"] = "Solo case",
            ["payload"] = new Dictionary<string, object?> { ["key"] = "MEMP-903", ["status"] = "ready" },
            ["dedupKey"] = "MEMP-903",
        }, cancellationToken: ct);
        Assert.True(ok.IsError is not true, "assemble without links should succeed: " + Text(ok));
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
        var skill = Text(get);
        Assert.Contains("MEMP-NNN", skill);
        // Structured output must emit null fields (not omit them), or strict clients reject the required-but-absent
        // field. This skill has no summary/project, so both keys must still be present as null.
        Assert.Contains("\"summary\"", skill);
        Assert.Contains("\"project\"", skill);
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
