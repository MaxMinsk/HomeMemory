using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Backlog;
using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Server.Security;
using MemoryMcp.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var transport = (Environment.GetEnvironmentVariable("MEMORY_TRANSPORT") ?? "stdio").ToLowerInvariant();
var dbPath = Environment.GetEnvironmentVariable("MEMORY_DB_PATH") ?? "memory.sqlite";

if (args.Length > 0 && (args[0] == "import-backlog" || args[0] == "export-backlog"))
{
    RunBacklogCli(args, dbPath);
    return;
}

if (transport == "http")
{
    var builder = WebApplication.CreateBuilder(args);
    RegisterServices(builder.Services, dbPath);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<IScopeAccessor, HttpScopeAccessor>();
    builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<MemoryTools>();

    var app = builder.Build();
    Bootstrap(app.Services);

    var token = Environment.GetEnvironmentVariable("MEMORY_BEARER_TOKEN");
    var allowedDomains = (Environment.GetEnvironmentVariable("MEMORY_ALLOWED_DOMAINS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    app.Use(async (context, next) =>
    {
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
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdio carries the protocol on stdout, so all logs must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    RegisterServices(builder.Services, dbPath);
    builder.Services.AddSingleton<IScopeAccessor, TrustedScopeAccessor>(); // local stdio is trusted
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<MemoryTools>();

    var app = builder.Build();
    Bootstrap(app.Services);
    await app.RunAsync();
}

static void RegisterServices(IServiceCollection services, string dbPath)
{
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<ISqliteConnectionFactory>(new SqliteConnectionFactory(dbPath));
    services.AddSingleton(SchemaRegistry.FromEmbeddedResources());
    services.AddSingleton<NotesRepository>();
    services.AddSingleton<DiagnosticsService>();
}

static void Bootstrap(IServiceProvider services)
{
    var factory = services.GetRequiredService<ISqliteConnectionFactory>();
    new Migrator(factory, SchemaMigrations.All).Migrate();
    services.GetRequiredService<SchemaRegistry>().SyncToDatabase(factory);
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
        var path = args.Length > 1 ? args[1] : throw new ArgumentException("Usage: import-backlog <file>");
        var count = BacklogImporter.Import(repository, File.ReadAllText(path));
        Console.WriteLine($"Imported {count} backlog items into '{dbPath}'.");
    }
    else
    {
        var path = args.Length > 1 ? args[1] : throw new ArgumentException("Usage: export-backlog <file>");
        File.WriteAllText(path, BacklogExporter.Export(repository));
        Console.WriteLine($"Exported backlog to '{path}'.");
    }
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
