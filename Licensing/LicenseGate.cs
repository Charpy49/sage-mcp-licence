using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Sage100Mcp.Licensing;

/// <summary>
/// Point unique de la vérification de licence, partagé par les deux transports (stdio et HTTP).
/// <para>
/// ⚠️ La vérification est <b>désactivée</b> : le corps des deux méthodes est commenté, exactement
/// comme il l'était dans <c>Program.cs</c>. Décommenter ici l'active pour stdio <b>et</b> HTTP
/// à la fois — c'est le seul endroit à modifier.
/// </para>
/// </summary>
public static class LicenseGate
{
    /// <summary>
    /// Valide la licence au démarrage. Renvoie <c>null</c> quand la vérification est désactivée
    /// (aucune restriction n'est alors appliquée).
    /// </summary>
    public static async Task<LicenseValidationResult?> ValidateOrExitAsync(
        IConfiguration configuration, CancellationToken cancellationToken)
    {
        /*
        var licenseOptions = configuration.GetSection(LicenseOptions.SectionName).Get<LicenseOptions>() ?? new LicenseOptions();

        var license = await LicenseClient.ValidateAsync(licenseOptions, cancellationToken);
        if (!license.Success)
        {
            Console.Error.WriteLine($"[Licence] Démarrage refusé : {license.ErrorMessage}");
            Environment.Exit(1);
        }
        Console.Error.WriteLine($"[Licence] Licence valide pour {license.ClientName}" +
            (license.AllowedTools is null ? " (tous les outils autorisés)." : $" ({license.AllowedTools.Count} outil(s) autorisé(s))."));
        return license;
        */

        return await Task.FromResult<LicenseValidationResult?>(null);
    }

    /// <summary>
    /// Retire de <c>McpServerOptions.ToolCollection</c> les outils non autorisés par la licence :
    /// ils n'apparaissent alors même pas dans <c>tools/list</c>. Sans effet si la licence est
    /// <c>null</c> ou si elle n'impose aucune liste.
    /// </summary>
    public static void RestrictTools(IServiceCollection services, LicenseValidationResult? license)
    {
        /*
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
        */
    }
}
