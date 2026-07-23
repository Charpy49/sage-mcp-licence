namespace Sage100Mcp.LicenseServer.Licensing;

public sealed class LicenseRecord
{
    public required string Id { get; init; }
    public required string ClientName { get; set; }
    public required string KeyHash { get; init; }
    public required string KeyPrefix { get; init; }

    /// <summary>Null = aucune restriction (tous les outils autorisés). Sinon, liste blanche des noms d'outils MCP.</summary>
    public IReadOnlyList<string>? AllowedTools { get; set; }

    public required DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsRevoked { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? LastValidatedAtUtc { get; set; }
    public string? LastValidatedIp { get; set; }
}
