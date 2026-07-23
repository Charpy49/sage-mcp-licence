namespace Sage100Mcp.LicenseServer.Licensing;

public sealed record ValidateRequest(string LicenseKey);

public sealed record ValidateResponse(
    bool Valid,
    string? Reason = null,
    string? ClientName = null,
    DateTimeOffset? ExpiresAtUtc = null,
    IReadOnlyList<string>? AllowedTools = null);

public sealed record CreateLicenseRequest(string ClientName, DateTimeOffset ExpiresAtUtc, IReadOnlyList<string>? AllowedTools);

public sealed record CreateLicenseResponse(string Id, string LicenseKey, string ClientName, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string>? AllowedTools);

/// <summary>
/// AllowedTools : si fourni (même liste vide), remplace la liste blanche. Laisser null pour ne pas y toucher.
/// ClearAllowedTools : true pour repasser la licence en "tous outils autorisés" (efface la restriction).
/// </summary>
public sealed record UpdateLicenseRequest(string? ClientName, DateTimeOffset? ExpiresAtUtc, bool? IsRevoked,
    IReadOnlyList<string>? AllowedTools, bool? ClearAllowedTools);

public sealed record LicenseSummary(string Id, string ClientName, string KeyPrefix, DateTimeOffset ExpiresAtUtc,
    bool IsRevoked, IReadOnlyList<string>? AllowedTools, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastValidatedAtUtc, string? LastValidatedIp)
{
    public static LicenseSummary From(LicenseRecord r) => new(
        r.Id, r.ClientName, r.KeyPrefix, r.ExpiresAtUtc, r.IsRevoked, r.AllowedTools, r.CreatedAtUtc,
        r.LastValidatedAtUtc, r.LastValidatedIp);
}
