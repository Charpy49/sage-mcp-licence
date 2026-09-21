namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>
/// Une version publiée du serveur MCP, telle que servie aux clients dans le manifeste de mise à jour.
/// </summary>
public sealed class ReleaseRecord
{
    /// <summary>Version au format <c>Major.Minor.Patch</c> (comparable via <see cref="System.Version"/>).</summary>
    public required string Version { get; init; }

    /// <summary>Canal de diffusion : <c>stable</c> par défaut, ou tout autre nom (ex. <c>beta</c>) pour un pilote.</summary>
    public required string Channel { get; set; }

    /// <summary>URL du paquet à télécharger (zip de publication).</summary>
    public required string DownloadUrl { get; set; }

    /// <summary>Empreinte SHA-256 du paquet, en hexadécimal. Vérifiée après téléchargement.</summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// Signature détachée du manifeste, à vérifier avant d'activer la version.
    /// Null tant que la signature n'est pas mise en place (le SHA-256 seul ne protège pas
    /// d'un attaquant qui contrôle le point de téléchargement).
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>Notes de version, affichables côté client.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Version plancher : tout client en dessous doit se mettre à jour de façon bloquante.
    /// À réserver aux correctifs de sécurité et aux ruptures de protocole.
    /// </summary>
    public bool IsMinimum { get; set; }

    /// <summary>Version retirée (coupe-circuit) : plus jamais servie, y compris aux clients épinglés dessus.</summary>
    public bool IsYanked { get; set; }

    public required DateTimeOffset PublishedAtUtc { get; init; }
}
