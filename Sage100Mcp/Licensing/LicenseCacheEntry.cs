namespace Sage100Mcp.Licensing;

/// <summary>Dernier résultat de validation en ligne connu, conservé pour couvrir les pannes réseau courtes.</summary>
public sealed class LicenseCacheEntry
{
    public required string LicenseKeyHash { get; init; }
    public required string ClientName { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public required DateTimeOffset ValidatedAtUtc { get; init; }
}
