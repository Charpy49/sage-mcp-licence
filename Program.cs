using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sage100Mcp.Configuration;
using Sage100Mcp.Data;

// Le content root pointe sur le dossier de l'exécutable (et non le répertoire courant),
// pour que appsettings.json soit trouvé quel que soit l'endroit d'où le client MCP lance le process.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// IMPORTANT : en transport stdio, les logs doivent partir sur stderr,
// car stdout est réservé au protocole MCP (JSON-RPC).
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<SageOptions>(builder.Configuration.GetSection(SageOptions.SectionName));
builder.Services.AddSingleton<SageDatabaseRegistry>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
