namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>
/// InstalledVersion / Transport : remontés par le serveur MCP pour l'inventaire du parc.
/// Optionnels — les clients antérieurs à la gestion des mises à jour ne les envoient pas.
/// </summary>
public sealed record ValidateRequest(string LicenseKey, string? InstalledVersion = null, string? Transport = null);

public sealed record ValidateResponse(
    bool Valid,
    string? Reason = null,
    string? ClientName = null,
    DateTimeOffset? ExpiresAtUtc = null,
    IReadOnlyList<string>? AllowedTools = null,
    UpdateManifest? Update = null);

/// <summary>
/// Manifeste de mise à jour servi au client. <see cref="LatestVersion"/> est la version que
/// <b>ce</b> client doit viser (elle tient compte de son épinglage et de son canal) ;
/// <see cref="MinimumVersion"/> est le plancher en dessous duquel la mise à jour est obligatoire.
/// </summary>
public sealed record UpdateManifest(
    string? LatestVersion,
    string? MinimumVersion,
    string? DownloadUrl,
    string? Sha256,
    string? Signature,
    string? ReleaseNotes)
{
    public static readonly UpdateManifest None = new(null, null, null, null, null, null);
}

public sealed record CreateLicenseRequest(string ClientName, DateTimeOffset ExpiresAtUtc, IReadOnlyList<string>? AllowedTools);

public sealed record CreateLicenseResponse(string Id, string LicenseKey, string ClientName, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string>? AllowedTools);

/// <summary>
/// AllowedTools : si fourni (même liste vide), remplace la liste blanche. Laisser null pour ne pas y toucher.
/// ClearAllowedTools : true pour repasser la licence en "tous outils autorisés" (efface la restriction).
/// </summary>
public sealed record UpdateLicenseRequest(string? ClientName, DateTimeOffset? ExpiresAtUtc, bool? IsRevoked,
    IReadOnlyList<string>? AllowedTools, bool? ClearAllowedTools);

/// <summary>
/// Politique de mise à jour d'une licence.
/// UpdateChannel : canal suivi par ce client ("stable" par défaut, "beta" pour un pilote).
/// PinnedVersion : gèle ce client sur une version précise (ex. pendant une clôture annuelle).
/// ClearPinnedVersion : true pour lever l'épinglage et revenir au suivi du canal.
/// </summary>
public sealed record UpdatePolicyRequest(string? UpdateChannel, string? PinnedVersion, bool? ClearPinnedVersion);

public sealed record LicenseSummary(string Id, string ClientName, string KeyPrefix, DateTimeOffset ExpiresAtUtc,
    bool IsRevoked, IReadOnlyList<string>? AllowedTools, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastValidatedAtUtc, string? LastValidatedIp,
    string UpdateChannel, string? PinnedVersion, string? LastInstalledVersion, string? LastTransport)
{
    public static LicenseSummary From(LicenseRecord r) => new(
        r.Id, r.ClientName, r.KeyPrefix, r.ExpiresAtUtc, r.IsRevoked, r.AllowedTools, r.CreatedAtUtc,
        r.LastValidatedAtUtc, r.LastValidatedIp,
        r.UpdateChannel, r.PinnedVersion, r.LastInstalledVersion, r.LastTransport);
}

public sealed record CreateReleaseRequest(string Version, string DownloadUrl, string Sha256,
    string? Channel = null, string? Signature = null, string? Notes = null, bool IsMinimum = false);
