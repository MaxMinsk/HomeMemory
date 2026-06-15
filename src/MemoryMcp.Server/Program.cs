using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Backlog;
using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Server.Logging;
using MemoryMcp.Server.Security;
using MemoryMcp.Server.Tools;
using MemoryMcp.Server.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Global regex match timeout: bounds any pattern, including user/agent-authored JSON Schema `pattern`s
// evaluated by JsonSchema.Net, against ReDoS (MEMP-060).
AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(2));

var transport = (Environment.GetEnvironmentVariable("MEMORY_TRANSPORT") ?? "stdio").ToLowerInvariant();
var dbPath = Environment.GetEnvironmentVariable("MEMORY_DB_PATH") ?? "memory.sqlite";

// Forced, cross-domain authoring guidance returned in `initialize` so every agent sees it on connect.
const string ServerInstructions =
    "Shared structured memory used by several agents. Model: domain = a project/workspace namespace " +
    "(lowercase slug, e.g. memory-mcp/kitchen/home); type = a schema-validated note kind; tags = " +
    "cross-cutting facets (loc:, cuisine:, have/want). Set a stable dedupKey for idempotent, editable " +
    "upserts; put structure in payload, free prose in body, facets in tags. Bytes go through artifacts, " +
    "never the model context. Relate notes with notes_link (active-voice lower_snake_case rel). " +
    "To start: call status (notesByType/notesByDomain), schema_list_types + schema_get(type) before " +
    "writing a typed note, and skill_list(domain)/skill_get for conventions. " +
    "READ FIRST: skill_get(domain=\"memory-mcp\", key=\"memory-authoring\") — the core authoring guide.";

if (args.Length > 0 && (args[0] == "import-backlog" || args[0] == "export-backlog"))
{
    RunBacklogCli(args, dbPath);
    return;
}

if (args.Length > 0 && args[0] == "push-backlog")
{
    await RunPushBacklog(args);
    return;
}

if (transport == "http")
{
    var builder = WebApplication.CreateBuilder(args);
    SuppressExpectedErrorLogs(builder.Services);
    RegisterServices(builder.Services, dbPath);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<IScopeAccessor, HttpScopeAccessor>();
    builder.Services.AddMcpServer(options => options.ServerInstructions = ServerInstructions)
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<MemoryTools>().WithTools<SkillTools>().WithTools<ArtifactTools>();

    var app = builder.Build();
    Bootstrap(app.Services);

    var token = Environment.GetEnvironmentVariable("MEMORY_BEARER_TOKEN");
    var allowedDomains = (Environment.GetEnvironmentVariable("MEMORY_ALLOWED_DOMAINS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    app.Use(async (context, next) =>
    {
        // The static viewer page loads without a token; its /api calls carry the bearer like /mcp.
        if (context.Request.Path.StartsWithSegments("/ui") || context.Request.Path == "/")
        {
            await next();
            return;
        }

        // /artifacts links use a short-lived signed URL (no bearer in the URL): a read link signs the id,
        // an upload link (/artifacts/upload) signs the destination params. Bearer also authorizes.
        if (context.Request.Path.StartsWithSegments("/artifacts", out var rest))
        {
            var signer = context.RequestServices.GetRequiredService<ArtifactUrlSigner>();
            var query = context.Request.Query;
            var hasBearer = !string.IsNullOrEmpty(token) && HasValidBearer(context, token);
            var ok = string.Equals(rest.Value?.Trim('/'), "upload", StringComparison.Ordinal)
                ? hasBearer || signer.VerifyUpload(query["domain"], query["filename"], query["contentType"], query["noteId"], query["exp"], query["sig"])
                : hasBearer || signer.Verify(rest.Value?.Trim('/') ?? string.Empty, query["exp"], query["sig"]);
            if (!ok)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
            return;
        }

        if (!string.IsNullOrEmpty(token) && !HasValidBearer(context, token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[HttpScopeAccessor.ScopeKey] = allowedDomains.Length == 0
            ? RequestScope.Unrestricted
            : RequestScope.ForDomains(allowedDomains);
        await next();
    });

    app.MapMcp("/mcp");
    app.MapViewer();
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdio carries the protocol on stdout, so all logs must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    SuppressExpectedErrorLogs(builder.Services);

    RegisterServices(builder.Services, dbPath);
    builder.Services.AddSingleton<IScopeAccessor, TrustedScopeAccessor>(); // local stdio is trusted
    builder.Services.AddMcpServer(options => options.ServerInstructions = ServerInstructions)
        .WithStdioServerTransport()
        .WithTools<MemoryTools>().WithTools<SkillTools>().WithTools<ArtifactTools>();

    var app = builder.Build();
    Bootstrap(app.Services);
    await app.RunAsync();
}

// Wrap every registered logger provider so expected, model-visible errors (McpException) are not
// logged as server faults. Applied after providers are registered, before the host is built.
static void SuppressExpectedErrorLogs(IServiceCollection services)
{
    for (var i = 0; i < services.Count; i++)
    {
        var descriptor = services[i];
        if (descriptor.ServiceType != typeof(ILoggerProvider))
        {
            continue;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(
                provider => new ExpectedErrorFilteringLoggerProvider((ILoggerProvider)factory(provider)));
        }
        else if (descriptor.ImplementationInstance is ILoggerProvider instance)
        {
            services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(new ExpectedErrorFilteringLoggerProvider(instance));
        }
        else if (descriptor.ImplementationType is { } type)
        {
            services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(
                provider => new ExpectedErrorFilteringLoggerProvider((ILoggerProvider)ActivatorUtilities.CreateInstance(provider, type)));
        }
    }
}

// Blob store lives next to the database (e.g. /data/blobs) unless overridden; default 1 GiB quota.
static BlobStore BuildBlobStore(string dbPath)
{
    var root = Environment.GetEnvironmentVariable("MEMORY_BLOB_ROOT")
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "blobs");
    var quota = long.TryParse(Environment.GetEnvironmentVariable("MEMORY_BLOB_QUOTA_BYTES"), out var bytes) && bytes > 0
        ? bytes
        : 1L * 1024 * 1024 * 1024;
    return new BlobStore(root, quota);
}

static void RegisterServices(IServiceCollection services, string dbPath)
{
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<ISqliteConnectionFactory>(new SqliteConnectionFactory(dbPath));
    services.AddSingleton(SchemaRegistry.FromEmbeddedResources());
    services.AddSingleton<NotesRepository>();
    services.AddSingleton<SkillsService>();
    services.AddSingleton<ConfirmationService>();
    services.AddSingleton(BuildBlobStore(dbPath));
    services.AddSingleton<ArtifactsService>();
    services.AddSingleton(new ArtifactIngestOptions(Environment.GetEnvironmentVariable("MEMORY_INGEST_ROOT")));
    services.AddSingleton(provider => new ArtifactUrlSigner(
        Environment.GetEnvironmentVariable("MEMORY_BEARER_TOKEN"), provider.GetRequiredService<TimeProvider>(),
        Environment.GetEnvironmentVariable("MEMORY_PUBLIC_BASE_URL")));
    services.AddSingleton<DiagnosticsService>();
}

static void Bootstrap(IServiceProvider services)
{
    var factory = services.GetRequiredService<ISqliteConnectionFactory>();
    new Migrator(factory, SchemaMigrations.All).Migrate();
    var registry = services.GetRequiredService<SchemaRegistry>();
    registry.SyncToDatabase(factory);     // seed/refresh built-ins
    registry.LoadFromDatabase(factory);   // pick up agent-authored schemas

    // Log a one-line stats snapshot at startup so the add-on log shows what is stored.
    var stats = services.GetRequiredService<DiagnosticsService>().Snapshot();
    var byType = string.Join(", ", stats.NotesByType.Select(pair => $"{pair.Key}={pair.Value}"));
    services.GetRequiredService<ILoggerFactory>().CreateLogger("MemoryMcp.Stats").LogInformation(
        "Memory stats: schema v{Schema}, {Notes} notes ({ByType}), {Attachments} attachments, {Bytes} blob bytes.",
        stats.SchemaVersion, stats.NoteCount, byType, stats.AttachmentCount, stats.BlobBytes);
}

static void RunBacklogCli(string[] args, string dbPath)
{
    var factory = new SqliteConnectionFactory(dbPath);
    new Migrator(factory, SchemaMigrations.All).Migrate();
    var registry = SchemaRegistry.FromEmbeddedResources();
    registry.SyncToDatabase(factory);
    var repository = new NotesRepository(factory, registry);

    if (args[0] == "import-backlog")
    {
        var path = args.Length > 1 ? args[1] : throw new InvalidOperationException("Usage: import-backlog <file>");
        var count = BacklogImporter.Import(repository, File.ReadAllText(path));
        Console.WriteLine($"Imported {count} backlog items into '{dbPath}'.");
    }
    else
    {
        var path = args.Length > 1 ? args[1] : throw new InvalidOperationException("Usage: export-backlog <file>");
        File.WriteAllText(path, BacklogExporter.Export(repository));
        Console.WriteLine($"Exported backlog to '{path}'.");
    }
}

static async Task RunPushBacklog(string[] args)
{
    if (args.Length < 4)
    {
        throw new InvalidOperationException("Usage: push-backlog <file> <url> <token> [domain]");
    }

    var file = args[1];
    var url = args[2];
    var token = args[3];
    var domain = args.Length > 4 ? args[4] : "memory-mcp";

    var items = BacklogImporter.Parse(File.ReadAllText(file));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var id = 1;
    var ok = 0;
    var failed = 0;
    foreach (var item in items)
    {
        var payload = JsonSerializer.Serialize(new { key = item.Key, status = item.Status });
        var arguments = new Dictionary<string, object?>
        {
            ["domain"] = domain,
            ["type"] = "backlog_item",
            ["title"] = item.Title,
            ["body"] = string.IsNullOrWhiteSpace(item.Body) ? null : item.Body,
            ["payload"] = payload,
            ["dedupKey"] = item.Key,
            ["sourceAgent"] = "backlog-importer",
        };
        var rpc = new
        {
            jsonrpc = "2.0",
            id = id++,
            method = "tools/call",
            @params = new { name = "notes_upsert", arguments },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(rpc), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode && !body.Contains("\"isError\":true", StringComparison.Ordinal))
        {
            ok++;
        }
        else
        {
            failed++;
            Console.Error.WriteLine($"{item.Key}: HTTP {(int)response.StatusCode} {body}");
        }
    }

    Console.WriteLine($"Pushed {ok} items to {url} (failed {failed}).");
}

static bool HasValidBearer(HttpContext context, string expected)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
    var configured = Encoding.UTF8.GetBytes(expected);
    return CryptographicOperations.FixedTimeEquals(presented, configured);
}
