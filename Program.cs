using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Sage100Mcp.Configuration;
using Sage100Mcp.Hosting;
using Sage100Mcp.Licensing;

// Deux transports dans un seul exécutable :
//
//   Sage100Mcp                 → stdio (défaut) : le client MCP lance le process, JSON-RPC sur stdout.
//   Sage100Mcp --http          → Streamable HTTP : un service pour N clients, éventuellement distants.
//
// Le transport peut aussi être imposé par la variable d'environnement MCP_TRANSPORT=http,
// utile pour un service Windows dont on ne maîtrise pas la ligne de commande.
var transport = McpTransportSelection.Resolve(args);

// Nos propres arguments sont retirés avant l'hôte : le fournisseur de configuration en ligne
// de commande prendrait "--http" pour une clé et consommerait l'argument suivant comme valeur.
var hostArgs = McpTransportSelection.StripTransportArgs(args);

if (transport == McpTransport.Http)
    await RunHttpAsync(hostArgs);
else
    await RunStdioAsync(hostArgs);

return;

static async Task RunStdioAsync(string[] args)
{
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

    var license = await LicenseGate.ValidateOrExitAsync(builder.Configuration, CancellationToken.None);

    builder.Services
        .AddSageMcpServer(builder.Configuration)
        .WithStdioServerTransport();

    LicenseGate.RestrictTools(builder.Services, license);

    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string[] args)
{
    // Même content root qu'en stdio : le service peut être démarré depuis n'importe quel répertoire.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    // Ici stdout n'est plus réservé au JSON-RPC : on garde le logging console standard.
    var httpOptions = builder.Configuration.GetSection(McpHttpOptions.SectionName).Get<McpHttpOptions>()
                      ?? new McpHttpOptions();

    // "urls" couvre à la fois --urls et ASPNETCORE_URLS ; ils restent prioritaires sur appsettings.
    var urls = builder.Configuration["urls"];
    if (string.IsNullOrWhiteSpace(urls))
    {
        urls = string.IsNullOrWhiteSpace(httpOptions.Urls) ? McpHttpOptions.DefaultUrls : httpOptions.Urls!;
        builder.WebHost.UseUrls(McpHttpSecurity.SplitUrls(urls));
    }

    var license = await LicenseGate.ValidateOrExitAsync(builder.Configuration, CancellationToken.None);

    builder.Services
        .AddSageMcpServer(builder.Configuration)
        .WithHttpTransport();

    LicenseGate.RestrictTools(builder.Services, license);

    var app = builder.Build();

    // Fail closed : pas d'exposition réseau des données comptables sans clé d'API.
    try
    {
        McpHttpSecurity.EnsureSafeConfiguration(httpOptions, urls, app.Logger);
    }
    catch (InvalidOperationException ex)
    {
        // Erreur de configuration, pas un bug : message lisible plutôt qu'une pile d'appels.
        Console.Error.WriteLine($"[Configuration] {ex.Message}");
        Environment.Exit(1);
    }

    app.UseMcpHttpSecurity(httpOptions);

    // Supervision (sans clé) : ne renvoie rien d'exploitable, ni bases ni chaînes de connexion.
    app.MapGet(McpHttpSecurity.HealthPath, () => Results.Json(new { statut = "ok" }));

    app.MapMcp(httpOptions.EndpointPath);

    app.Logger.LogInformation("Serveur Sage MCP (HTTP) à l'écoute sur {Urls}{Endpoint}", urls, httpOptions.EndpointPath);

    await app.RunAsync();
}
