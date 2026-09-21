using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Sage100Mcp.Configuration;
using Sage100Mcp.Data;

namespace Sage100Mcp.Hosting;

/// <summary>Enregistrements communs aux deux transports (le transport est ajouté par l'appelant).</summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre l'annuaire des bases Sage et les 44 outils <c>sage_*</c> découverts par réflexion.
    /// L'appelant chaîne ensuite <c>.WithStdioServerTransport()</c> ou <c>.WithHttpTransport()</c>.
    /// </summary>
    public static IMcpServerBuilder AddSageMcpServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SageOptions>(configuration.GetSection(SageOptions.SectionName));
        services.AddSingleton<SageDatabaseRegistry>();

        return services
            .AddMcpServer()
            .WithToolsFromAssembly();
    }
}
