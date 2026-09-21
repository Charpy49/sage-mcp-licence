using System.Reflection;

namespace Sage100Mcp.Hosting;

/// <summary>
/// Version de l'assembly, telle que remontée dans <c>serverInfo.version</c> et au serveur de licences.
/// Provient de <c>&lt;Version&gt;</c> dans le .csproj.
/// </summary>
public static class AppVersion
{
    /// <summary>Version au format <c>Major.Minor.Patch</c> (ex. "1.1.0").</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;

        // InformationalVersion porte la valeur littérale de <Version>, mais SourceLink y ajoute
        // "+<sha du commit>" : on ne garde que la partie version.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        // Repli : AssemblyVersion est toujours en 4 composants ("1.1.0.0"), on en garde 3.
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
