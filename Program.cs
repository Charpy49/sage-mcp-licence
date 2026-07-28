using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Sage100Mcp.Configuration;
using Sage100Mcp.Data;
using Sage100Mcp.Licensing;

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
/*
var licenseOptions = builder.Configuration.GetSection(LicenseOptions.SectionName).Get<LicenseOptions>() ?? new LicenseOptions();

var license = await LicenseClient.ValidateAsync(licenseOptions, CancellationToken.None);
if (!license.Success)
{
    Console.Error.WriteLine($"[Licence] Démarrage refusé : {license.ErrorMessage}");
    Environment.Exit(1);
    return;
}
Console.Error.WriteLine($"[Licence] Licence valide pour {license.ClientName}" +
    (license.AllowedTools is null ? " (tous les outils autorisés)." : $" ({license.AllowedTools.Count} outil(s) autorisé(s))."));
*/
builder.Services.Configure<SageOptions>(builder.Configuration.GetSection(SageOptions.SectionName));
builder.Services.AddSingleton<SageDatabaseRegistry>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Restreint les outils exposés à ceux autorisés par la licence (null = pas de restriction).
/*if (license.AllowedTools is { } allowedTools)
{
    builder.Services.PostConfigure<McpServerOptions>(options =>
    {
        foreach (var tool in options.ToolCollection?.ToArray() ?? [])
        {
            if (tool.ProtocolTool?.Name is not { } name || !allowedTools.Contains(name))
            {
                options.ToolCollection!.Remove(tool);
            }
        }
    });
}*/

await builder.Build().RunAsync();
