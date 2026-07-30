using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MemoryMcp.Tests.Integration;

/// <summary>
/// MEMP-233: the class-level guard behind MEMP-232. Fixing tools one at a time does not stop the next one
/// from skipping the scope check — tags_list, domains_list and status all shipped without it. This asserts
/// the invariant for the WHOLE advertised read surface: it enumerates every tool the running server reports
/// with <c>readOnlyHint</c>, calls each one under a token scoped to a single domain, and fails if any
/// response carries content from a domain the caller may not read. A tool added later is covered the moment
/// it appears in <c>tools/list</c> — nothing has to be added here.
/// </summary>
public class ReadToolScopeGuardTests
{
    private const string InScopeDomain = "home";
    private const string OutOfScopeDomain = "offlimits";

    // Seeded only in the out-of-scope domain, in the title, body, tags AND dedup key — so a leak through any
    // of those surfaces trips the assertion. Distinctive enough that a substring match means something.
    private const string Canary = "CANARY7F3A";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Every_advertised_read_tool_hides_out_of_scope_notes()
    {
        using var temp = new TempDatabase();
        using var blobDir = new TempDir();
        const string token = "guard-bearer-token";

        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var notes = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        Seed(notes, InScopeDomain, "MEMP-700", "home-note");
        Seed(notes, OutOfScopeDomain, "OFF-700", Canary);

        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var server = StartHttpServer(temp.FilePath, blobDir.Path, token, port, InScopeDomain);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitForReady(http, cts.Token);

            await using var client = await Connect(port, token, cts.Token);
            var readTools = (await client.ListToolsAsync(cancellationToken: cts.Token))
                .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
                .ToList();

            Assert.True(readTools.Count > 20, $"expected the full read surface, saw {readTools.Count} tools");

            var leaked = new List<string>();
            foreach (var tool in readTools)
            {
                var rendered = await Call(client, tool, cts.Token);
                // Case-insensitive: tags and domains are normalized to lower case on write, so an
                // ordinal match would sail straight past a leaked "tag:canary7f3a".
                if (rendered.Contains(Canary, StringComparison.OrdinalIgnoreCase) ||
                    rendered.Contains(OutOfScopeDomain, StringComparison.OrdinalIgnoreCase))
                {
                    leaked.Add($"{tool.Name}: {Trim(rendered)}");
                }
            }

            Assert.True(leaked.Count == 0,
                "These read tools returned content from a domain the caller may not read:\n  " + string.Join("\n  ", leaked));

            // The cheap way to pass the assertion above is to return nothing at all, so prove the in-scope
            // domain is still fully visible through the same tools.
            Assert.Contains("home-note", await Call(client, Tool(readTools, "notes_search"), cts.Token), StringComparison.Ordinal);
            Assert.Contains(InScopeDomain, await Call(client, Tool(readTools, "domains_list"), cts.Token), StringComparison.Ordinal);
            Assert.Contains("tag:home-note", await Call(client, Tool(readTools, "tags_list"), cts.Token), StringComparison.Ordinal);
        }
        finally
        {
            TryKill(server);
        }
    }

    private static McpClientTool Tool(IEnumerable<McpClientTool> tools, string name) =>
        tools.First(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    // Calls a tool with its declared required arguments filled in. Unknown ids/keys resolve to nothing, which
    // is exactly right here: the test looks for leakage, not for hits. An error result is a pass — a refusal
    // is the guard working — so its text is scanned too, in case a message names an out-of-scope domain.
    private static async Task<string> Call(McpClient client, McpClientTool tool, CancellationToken ct)
    {
        var arguments = RequiredArguments(tool);
        try
        {
            var result = await client.CallToolAsync(tool.Name, arguments, cancellationToken: ct);
            // Read both channels: the text blocks and the structured payload. Serializing CallToolResult
            // wholesale would lean on polymorphic content serialization and can silently drop the text.
            return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text))
                + (result.StructuredContent?.GetRawText() ?? string.Empty);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return error.Message; // a thrown McpException is still a refusal, and its text must not leak either
        }
    }

    private static Dictionary<string, object?> RequiredArguments(McpClientTool tool)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("required", out var required))
        {
            return arguments;
        }

        schema.TryGetProperty("properties", out var properties);
        foreach (var name in required.EnumerateArray().Select(value => value.GetString()).OfType<string>())
        {
            var type = properties.ValueKind == JsonValueKind.Object &&
                       properties.TryGetProperty(name, out var property) &&
                       property.TryGetProperty("type", out var declared)
                ? TypeName(declared)
                : "string";

            // A required domain argument must stay in scope, or a tool "passes" merely by throwing 403.
            arguments[name] = name switch
            {
                "domain" => InScopeDomain,
                _ => type switch
                {
                    "integer" or "number" => 1,
                    "boolean" => false,
                    "array" => Array.Empty<string>(),
                    "object" => new Dictionary<string, object?>(),
                    _ => "no-such-value",
                },
            };
        }

        return arguments;
    }

    // A schema type may be "string" or ["string","null"]; take the first non-null entry.
    private static string TypeName(JsonElement declared) =>
        declared.ValueKind == JsonValueKind.Array
            ? declared.EnumerateArray().Select(entry => entry.GetString()).FirstOrDefault(name => name is not null and not "null") ?? "string"
            : declared.GetString() ?? "string";

    private static async Task<McpClient> Connect(int port, string token, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "scope-guard",
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });

        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static void Seed(NotesRepository notes, string domain, string key, string marker) =>
        notes.Upsert(domain, "backlog_item", marker, $"{marker} body",
            $$"""{ "key": "{{key}}", "status": "ready" }""", $"""["tag:{marker}"]""", key, "tester");

    private static string Trim(string rendered) => rendered.Length <= 400 ? rendered : rendered[..400] + "…";

    private static async Task WaitForReady(HttpClient http, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("/ui", ct); // /ui is auth-exempt
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // server not up yet
            }

            await Task.Delay(250, ct);
        }

        throw new InvalidOperationException("HTTP server did not become ready in time.");
    }

    private static Process StartHttpServer(string dbPath, string blobRoot, string token, int port, string allowedDomains)
    {
        var info = new ProcessStartInfo("dotnet", LocateServerDll())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["MEMORY_TRANSPORT"] = "http";
        info.Environment["MEMORY_DB_PATH"] = dbPath;
        info.Environment["MEMORY_BLOB_ROOT"] = blobRoot;
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        info.Environment["MEMORY_BEARER_TOKEN"] = token;
        info.Environment["MEMORY_ALLOWED_DOMAINS"] = allowedDomains;
        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start server process.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateServerDll() =>
        Environment.GetEnvironmentVariable("MEMORY_SERVER_DLL")
        ?? typeof(ReadToolScopeGuardTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "ServerDll").Value!;
}
