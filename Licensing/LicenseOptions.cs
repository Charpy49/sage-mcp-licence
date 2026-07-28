namespace Sage100Mcp.Licensing;

public sealed class LicenseOptions
{
    public const string SectionName = "License";

    /// <summary>URL de base du serveur de licences (ex. https://licences.exemple.com).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Clé de licence attribuée à ce client, générée par le serveur de licences.</summary>
    public string? LicenseKey { get; set; }

    /// <summary>Délai maximal (secondes) accordé à l'appel de validation en ligne avant timeout.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Nombre de jours pendant lesquels une licence validée avec succès reste acceptée depuis le cache local
    /// si le serveur de licences est injoignable au démarrage suivant.
    /// </summary>
    public int GracePeriodDays { get; set; } = 3;
}
