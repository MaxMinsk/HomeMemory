using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Core.Backlog;
using MemoryMcp.Core.Confirmation;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Skills;
using MemoryMcp.Core.Storage;
using MemoryMcp.Server.Logging;
using MemoryMcp.Server.Mqtt;
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
    "writing a typed note, and skill_list(domain)/skill_get for conventions. For a task in a domain, " +
    "memory_context(query, domain) loads its rules + skills + relevant notes in one call (and domain_manifest " +
    "for a plain overview). " +
    "For a large/unknown note, read a slice not the whole body: notes_get(includeBody=false) to peek, then " +
    "notes_outline/notes_find/notes_read; fetch the full body only when you truly need it. " +
    "Use Memory as durable working memory, not only when asked: at the start of a related task search for " +
    "relevant notes; during and after, save durable facts/decisions/preferences/project-state without waiting " +
    "to be told — but never secrets, and ask before sensitive personal info. Prefer updating existing notes " +
    "over duplicates, and mention briefly what you saved. " +
    "The 'commons' domain is shared and readable by every agent (even a domain-scoped one); it holds the core " +
    "rules/skills. READ FIRST: skill_list(domain=\"commons\"), then skill_get(domain=\"commons\", " +
    "key=\"agent-memory-use\") for when to recall/save and key=\"memory-authoring\" for how to write. " +
    "Memory is self-organizing but not self-authorizing: to improve another note (retag/summary/links/dedup), " +
    "do NOT silently mutate it — write a memory_evolution_suggestion note (target_id, proposed_patch/links, " +
    "rationale, status=open); a human/agent reviews and applies it via notes_patch/notes_link. " +
    "See skill_get(domain=\"commons\", key=\"agent-memory-enrichment\").";

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

if (args.Length > 0 && (args[0] == "gc-blobs" || args[0] == "normalize-identifiers"))
{
    RunMaintenance(args, dbPath);
    return;
}

if (args.Length > 0 && args[0] == "tokens")
{
    RunTokens(args, dbPath);
    return;
}

if (transport == "http")
{
    // Fail closed: HTTP mode must be authenticated (it sits behind HA/tunnels). Without a bearer, /mcp,
    // viewer APIs and artifact signing would be public/predictable — refuse to start unless explicitly
    // opted out for local dev (MEMP-100).
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEMORY_BEARER_TOKEN"))
        && !string.Equals(Environment.GetEnvironmentVariable("ALLOW_UNAUTHENTICATED_HTTP"), "true", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Refusing to start HTTP mode without MEMORY_BEARER_TOKEN. Set a bearer token, " +
            "or set ALLOW_UNAUTHENTICATED_HTTP=true for local dev only.");
    }

    var builder = WebApplication.CreateBuilder(args);
    SuppressExpectedErrorLogs(builder.Services);
    RegisterServices(builder.Services, dbPath);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<IScopeAccessor, HttpScopeAccessor>();
    builder.Services.AddMcpServer(options => options.ServerInstructions = ServerInstructions)
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<MemoryTools>(StructuredToolJson()).WithTools<SkillTools>(StructuredToolJson()).WithTools<ArtifactTools>(StructuredToolJson());

    var app = builder.Build();
    Bootstrap(app.Services);

    var authenticator = app.Services.GetRequiredService<BearerAuthenticator>();

    app.Use(async (context, next) =>
    {
        // The static viewer page loads without a token; its /api calls carry the bearer like /mcp.
        // /health is an unauthenticated liveness probe (HA watchdog / Docker healthcheck).
        if (context.Request.Path.StartsWithSegments("/ui") || context.Request.Path == "/" || context.Request.Path == "/health")
        {
            await next();
            return;
        }

        // Resolve the presented bearer to a scope (env root token OR a DB token's per-domain scope); null
        // means unauthenticated/unknown. In dev mode (no env token) this is always unrestricted (MEMP-032).
        var scope = authenticator.Authenticate(context);

        // /artifacts also accepts a short-lived signed URL as a capability (no bearer in the URL).
        if (context.Request.Path.StartsWithSegments("/artifacts", out var rest))
        {
            var signer = context.RequestServices.GetRequiredService<ArtifactUrlSigner>();
            var query = context.Request.Query;
            var signed = string.Equals(rest.Value?.Trim('/'), "upload", StringComparison.Ordinal)
                ? signer.VerifyUpload(query["domain"], query["filename"], query["contentType"], query["noteId"], query["exp"], query["sig"])
                : signer.Verify(rest.Value?.Trim('/') ?? string.Empty, query["exp"], query["sig"]);
            if (scope is null && !signed)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Signed URL = capability (already domain-bound); a bearer request carries the caller's scope.
            context.Items[HttpScopeAccessor.ScopeKey] = scope ?? RequestScope.Unrestricted;
            await next();
            return;
        }

        if (scope is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[HttpScopeAccessor.ScopeKey] = scope;
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
        .WithTools<MemoryTools>(StructuredToolJson()).WithTools<SkillTools>(StructuredToolJson()).WithTools<ArtifactTools>(StructuredToolJson());

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

// Strict MCP clients (e.g. Cursor) validate structured tool output against its schema, which the SDK marks
// all-properties-required. The SDK's default serializer omits nulls, so a required nullable field goes missing
// and validation fails. Emit nulls explicitly (the schema's nullable type accepts null). Clone the SDK
// defaults to keep its converters.
static JsonSerializerOptions StructuredToolJson() =>
    new(ModelContextProtocol.McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

static void RegisterServices(IServiceCollection services, string dbPath)
{
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<ISqliteConnectionFactory>(new SqliteConnectionFactory(dbPath));
    services.AddSingleton(SchemaRegistry.FromEmbeddedResources());
    RegisterMqtt(services);
    services.AddSingleton(provider => new NotesRepository(
        provider.GetRequiredService<ISqliteConnectionFactory>(),
        provider.GetRequiredService<SchemaRegistry>(),
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<INoteEventSink>()));
    services.AddSingleton<SkillsService>();
    services.AddSingleton<ConfirmationService>();
    services.AddSingleton(BuildBlobStore(dbPath));
    services.AddSingleton<ArtifactsService>();
    services.AddSingleton(new ArtifactIngestOptions(Environment.GetEnvironmentVariable("MEMORY_INGEST_ROOT")));
    // Prefer a dedicated signing key; fall back to the bearer token. (In HTTP mode a bearer is required —
    // see the fail-closed check — so the predictable built-in fallback never keys real artifact URLs.)
    services.AddSingleton(provider => new ArtifactUrlSigner(
        Environment.GetEnvironmentVariable("MEMORY_ARTIFACT_SIGNING_KEY")
            ?? Environment.GetEnvironmentVariable("MEMORY_BEARER_TOKEN"),
        provider.GetRequiredService<TimeProvider>(),
        Environment.GetEnvironmentVariable("MEMORY_PUBLIC_BASE_URL")));
    services.AddSingleton<DiagnosticsService>();
    services.AddSingleton<SuggestionReviewer>(); // applies/rejects memory_evolution_suggestion (viewer review actions)
    // Single domain-authorization facade (MEMP-102): wraps the request scope so every tool/endpoint
    // authorizes through one place. Safe as a singleton — it reads the ambient scope per call.
    services.AddSingleton<RequestAuthorizer>();
    services.AddSingleton<TokenStore>();
    services.AddSingleton(provider => new BearerAuthenticator(
        Environment.GetEnvironmentVariable("MEMORY_BEARER_TOKEN"),
        (Environment.GetEnvironmentVariable("MEMORY_ALLOWED_DOMAINS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        provider.GetRequiredService<TokenStore>()));
}

// MQTT is opt-in (MEMP-156 / MEMP-056). When disabled, the no-op sink is used and nothing connects to a
// broker. When enabled, register the shared connection, the note-change sink, and the HA discovery service.
static void RegisterMqtt(IServiceCollection services)
{
    var mqtt = MqttOptions.FromEnvironment();
    if (!mqtt.Enabled || string.IsNullOrWhiteSpace(mqtt.Host))
    {
        services.AddSingleton<INoteEventSink>(NullNoteEventSink.Instance);
        return;
    }

    services.AddSingleton(mqtt);
    services.AddSingleton<MqttConnection>();
    services.AddSingleton<INoteEventSink, MqttNoteEventSink>();
    services.AddHostedService<HomeAssistantDiscoveryService>();
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
        "Memory stats: schema v{Schema}, {Notes} notes ({ByType}), {Attachments} attachments, {Bytes} blob bytes, db {DbMb:0.##} MB.",
        stats.SchemaVersion, stats.NoteCount, byType, stats.AttachmentCount, stats.BlobBytes, stats.DbSizeBytes / 1048576.0);
}

// Admin maintenance subcommands (run via the binary, not model-facing): `gc-blobs [--apply]`,
// `normalize-identifiers [--apply]`. Without --apply they are a dry run that only reports.
static void RunMaintenance(string[] args, string dbPath)
{
    var factory = new SqliteConnectionFactory(dbPath);
    new Migrator(factory, SchemaMigrations.All).Migrate();
    var apply = args.Contains("--apply", StringComparer.Ordinal);

    if (args[0] == "gc-blobs")
    {
        var report = BlobReconciler.Reconcile(factory, BuildBlobStore(dbPath), apply);
        Console.WriteLine($"gc-blobs: {report.OrphanBlobs.Count} orphan blob(s), {report.FreedBytes} bytes" +
            (apply ? " freed." : " (dry run; pass --apply to delete)."));
        if (report.MissingBlobs.Count > 0)
        {
            Console.WriteLine($"  WARNING: {report.MissingBlobs.Count} attachment(s) reference a missing blob.");
        }

        return;
    }

    var backfill = IdentifierBackfill.Run(factory, apply);
    Console.WriteLine($"normalize-identifiers: {backfill.NotesUpdated} note(s), {backfill.AttachmentsUpdated} attachment(s)" +
        (apply ? " updated." : " would change (dry run; pass --apply to write)."));
    if (backfill.Collisions.Count > 0)
    {
        Console.WriteLine($"  WARNING: {backfill.Collisions.Count} note(s) skipped due to dedup collisions: {string.Join(", ", backfill.Collisions)}");
    }
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

// Admin token management (run via the binary): tokens add <label> <domains|*> | list | revoke <id>.
// Generates DB-backed bearer tokens with per-domain scope; the raw token is printed once on add.
static void RunTokens(string[] args, string dbPath)
{
    var factory = new SqliteConnectionFactory(dbPath);
    new Migrator(factory, SchemaMigrations.All).Migrate();
    var store = new TokenStore(factory);
    var sub = args.Length > 1 ? args[1] : "list";

    if (sub == "add")
    {
        var label = args.Length > 2 ? args[2] : null;
        var domains = args.Length > 3 ? args[3] : "*";
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var record = store.Add(label, domains, raw);
        Console.WriteLine($"Created token {record.Id} (label={record.Label ?? "-"}, domains={record.Domains}).");
        Console.WriteLine($"TOKEN (shown once, store it now): {raw}");
    }
    else if (sub == "revoke")
    {
        var id = args.Length > 2 ? args[2] : throw new InvalidOperationException("Usage: tokens revoke <id>");
        Console.WriteLine(store.Revoke(id) ? $"Revoked {id}." : $"No active token '{id}'.");
    }
    else
    {
        foreach (var token in store.List())
        {
            var state = token.RevokedUtc is null ? "active" : $"revoked {token.RevokedUtc}";
            Console.WriteLine($"{token.Id}  domains={token.Domains}  label={token.Label ?? "-"}  created={token.CreatedUtc}  {state}");
        }
    }
}
