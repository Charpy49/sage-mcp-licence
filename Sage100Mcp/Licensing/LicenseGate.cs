using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Sage100Mcp.Hosting;

namespace Sage100Mcp.Licensing;

/// <summary>
/// Point unique de la vérification de licence, partagé par les deux transports (stdio et HTTP).
/// Modifier ici agit sur stdio <b>et</b> HTTP à la fois.
/// </summary>
public static class LicenseGate
{
    /// <summary>
    /// Valide la licence au démarrage et met fin au process si elle est refusée. La valeur renvoyée
    /// alimente <see cref="RestrictTools"/> ; elle n'est jamais <c>null</c> en retour normal.
    /// </summary>
    public static async Task<LicenseValidationResult?> ValidateOrExitAsync(
        IConfiguration configuration, McpTransport transport, CancellationToken cancellationToken)
    {
        var licenseOptions = configuration.GetSection(LicenseOptions.SectionName).Get<LicenseOptions>() ?? new LicenseOptions();

        var license = await LicenseClient.ValidateAsync(licenseOptions, TransportName(transport), cancellationToken);
        if (!license.Success)
        {
            Console.Error.WriteLine($"[Licence] Démarrage refusé : {license.ErrorMessage}");
            Environment.Exit(1);
        }
        Console.Error.WriteLine($"[Licence] Licence valide pour {license.ClientName}" +
            (license.AllowedTools is null ? " (tous les outils autorisés)." : $" ({license.AllowedTools.Count} outil(s) autorisé(s))."));
        ReportUpdate(license.Update);
        return license;
    }

    /// <summary>Nom du transport tel que remonté au serveur de licences (inventaire du parc).</summary>
    public static string TransportName(McpTransport transport) =>
        transport == McpTransport.Http ? "http" : "stdio";

    /// <summary>
    /// Signale une mise à jour disponible sur stderr. Ce serveur ne se met pas à jour lui-même :
    /// le téléchargement et la bascule de version sont le rôle du shim de lancement, qui interroge
    /// <c>/api/version/check</c> indépendamment de la licence.
    /// </summary>
    public static void ReportUpdate(UpdateInfo? update)
    {
        if (update?.LatestVersion is null) return;

        var installed = AppVersion.Current;
        if (update.IsUpdateRequired(installed))
        {
            Console.Error.WriteLine(
                $"[Mise à jour] Version installée {installed} sous le plancher requis {update.MinimumVersion} : " +
                $"la mise à jour vers {update.LatestVersion} est obligatoire.");
        }
        else if (update.IsUpdateAvailable(installed))
        {
            Console.Error.WriteLine(
                $"[Mise à jour] Version {update.LatestVersion} disponible (installée : {installed}).");
        }
    }

    /// <summary>
    /// Retire de <c>McpServerOptions.ToolCollection</c> les outils non autorisés par la licence :
    /// ils n'apparaissent alors même pas dans <c>tools/list</c>. Sans effet si la licence est
    /// <c>null</c> ou si elle n'impose aucune liste.
    /// </summary>
    public static void RestrictTools(IServiceCollection services, LicenseValidationResult? license)
    {
        if (license?.AllowedTools is { } allowedTools)
        {
            services.PostConfigure<McpServerOptions>(options =>
            {
                foreach (var tool in options.ToolCollection?.ToArray() ?? [])
                {
                    if (tool.ProtocolTool?.Name is not { } name || !allowedTools.Contains(name))
                    {
                        options.ToolCollection!.Remove(tool);
                    }
                }
            });
        }
    }
}
