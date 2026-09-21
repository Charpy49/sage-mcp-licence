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

    // --- Politique de mise à jour ---

    /// <summary>Canal suivi par ce client ("stable" par défaut, "beta" pour un pilote).</summary>
    public string UpdateChannel { get; set; } = ReleaseRepository.DefaultChannel;

    /// <summary>
    /// Gèle ce client sur une version précise. Null = il suit la dernière version de son canal.
    /// Un épinglage sous le plancher de version est ignoré (voir <see cref="UpdateResolver"/>).
    /// </summary>
    public string? PinnedVersion { get; set; }

    // --- Inventaire, remonté par le client à chaque validation ---

    /// <summary>Dernière version signalée par ce client.</summary>
    public string? LastInstalledVersion { get; set; }

    /// <summary>Dernier transport signalé par ce client ("stdio" ou "http").</summary>
    public string? LastTransport { get; set; }
}
