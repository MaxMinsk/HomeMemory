using MemoryMcp.Core.Diagnostics;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var transport = (Environment.GetEnvironmentVariable("MEMORY_TRANSPORT") ?? "stdio").ToLowerInvariant();
var dbPath = Environment.GetEnvironmentVariable("MEMORY_DB_PATH") ?? "memory.sqlite";

if (transport == "http")
{
    var builder = WebApplication.CreateBuilder(args);
    RegisterServices(builder.Services, dbPath);
    builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryTools>();

    var app = builder.Build();
    Bootstrap(app.Services);
    app.MapMcp();
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdio carries the protocol on stdout, so all logs must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    RegisterServices(builder.Services, dbPath);
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
