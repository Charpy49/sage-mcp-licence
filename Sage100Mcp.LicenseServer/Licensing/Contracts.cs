namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>
/// InstalledVersion / Transport : remontés par le serveur MCP pour l'inventaire du parc.
/// MachineId / MachineName / Nonce : activation par poste et jeton signé (à partir de la 1.3.0).
/// Tous optionnels — les clients plus anciens ne les envoient pas.
/// </summary>
public sealed record ValidateRequest(string LicenseKey, string? InstalledVersion = null, string? Transport = null,
    string? MachineId = null, string? MachineName = null, string? Nonce = null);

/// <summary>
/// Token / TokenSignature : jeton de licence signé (voir <see cref="LicenseToken"/>), seule source de vérité
/// pour le client ; les champs en clair ne sont conservés que pour les clients antérieurs à la 1.3.0.
/// ActivatedMachines / MaxMachines : renseignés sur un refus <c>machine_limit</c>, pour que le message
/// d'erreur nomme les postes qui occupent la licence.
/// </summary>
public sealed record ValidateResponse(
    bool Valid,
    string? Reason = null,
    string? ClientName = null,
    DateTimeOffset? ExpiresAtUtc = null,
    IReadOnlyList<string>? AllowedTools = null,
    UpdateManifest? Update = null,
    string? Token = null,
    string? TokenSignature = null,
    IReadOnlyList<string>? ActivatedMachines = null,
    int? MaxMachines = null);

/// <summary>
/// Contenu du jeton de licence signé. Le client vérifie la signature avec la clé publique embarquée,
/// puis que <see cref="MachineId"/>, <see cref="LicenseKeyHash"/> et <see cref="Nonce"/> sont bien les siens :
/// un jeton ne peut donc être ni fabriqué, ni transplanté sur un autre poste, ni rejoué en ligne.
/// Hors connexion, il reste accepté jusqu'à <see cref="OfflineUntilUtc"/>.
/// </summary>
public sealed record LicenseToken(
    int V,
    string LicenseKeyHash,
    string MachineId,
    string ClientName,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string>? AllowedTools,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset OfflineUntilUtc,
    string? Nonce);

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

/// <summary>MaxMachines : nombre de postes pouvant activer la licence (1 par défaut).</summary>
public sealed record CreateLicenseRequest(string ClientName, DateTimeOffset ExpiresAtUtc, IReadOnlyList<string>? AllowedTools,
    int? MaxMachines = null);

public sealed record CreateLicenseResponse(string Id, string LicenseKey, string ClientName, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string>? AllowedTools, int MaxMachines);

/// <summary>
/// AllowedTools : si fourni (même liste vide), remplace la liste blanche. Laisser null pour ne pas y toucher.
/// ClearAllowedTools : true pour repasser la licence en "tous outils autorisés" (efface la restriction).
/// MaxMachines : nombre de postes autorisés. L'abaisser ne supprime aucune activation, mais seuls les
/// postes activés les premiers restent acceptés.
/// </summary>
public sealed record UpdateLicenseRequest(string? ClientName, DateTimeOffset? ExpiresAtUtc, bool? IsRevoked,
    IReadOnlyList<string>? AllowedTools, bool? ClearAllowedTools, int? MaxMachines = null);

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
    string UpdateChannel, string? PinnedVersion, string? LastInstalledVersion, string? LastTransport,
    int MaxMachines, IReadOnlyList<ActivationRecord> Machines)
{
    public static LicenseSummary From(LicenseRecord r, IReadOnlyList<ActivationRecord> machines) => new(
        r.Id, r.ClientName, r.KeyPrefix, r.ExpiresAtUtc, r.IsRevoked, r.AllowedTools, r.CreatedAtUtc,
        r.LastValidatedAtUtc, r.LastValidatedIp,
        r.UpdateChannel, r.PinnedVersion, r.LastInstalledVersion, r.LastTransport,
        r.MaxMachines, machines);
}

/// <summary>
/// Poste ayant activé une licence. MachineId est une empreinte (SHA-256) calculée par le client :
/// le serveur ne voit jamais l'identifiant matériel brut. IsActive = false : poste au-delà de MaxMachines
/// (après une baisse du quota), refusé tant qu'il n'est pas libéré ou que le quota n'est pas relevé.
/// </summary>
public sealed record ActivationRecord(string MachineId, string? MachineName, DateTimeOffset ActivatedAtUtc,
    DateTimeOffset LastSeenAtUtc, bool IsActive = true);

public sealed record CreateReleaseRequest(string Version, string DownloadUrl, string Sha256,
    string? Channel = null, string? Signature = null, string? Notes = null, bool IsMinimum = false);
