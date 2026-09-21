namespace Sage100Mcp.LicenseServer.Licensing;

/// <summary>
/// Détermine la version servie à un client donné. Trois leviers, du plus spécifique au plus général :
/// épinglage de la licence, canal de la licence, plancher de version (mise à jour forcée).
/// </summary>
public static class UpdateResolver
{
    /// <summary>
    /// Ordre de comparaison des versions. <see cref="System.Version"/> et non semver complet :
    /// les suffixes de préversion ("1.3.0-beta") ne sont pas gérés — c'est le canal qui joue ce rôle.
    /// </summary>
    public static Version ParseVersion(ReleaseRecord release) => ParseVersion(release.Version);

    public static Version ParseVersion(string version) =>
        Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0, 0);

    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && Version.TryParse(version, out _);

    public static UpdateManifest Resolve(
        IReadOnlyList<ReleaseRecord> releases, string? channel, string? pinnedVersion)
    {
        // Une version retirée n'est jamais servie, même à un client épinglé dessus.
        var available = releases.Where(r => !r.IsYanked).ToList();
        if (available.Count == 0) return UpdateManifest.None;

        var effectiveChannel = string.IsNullOrWhiteSpace(channel) ? ReleaseRepository.DefaultChannel : channel.Trim();

        // Le plancher s'applique tous canaux confondus : c'est un impératif de sécurité,
        // pas une préférence de diffusion.
        var minimum = available.Where(r => r.IsMinimum).MaxBy(ParseVersion);

        var target = pinnedVersion is not null
            ? available.FirstOrDefault(r => string.Equals(r.Version, pinnedVersion, StringComparison.OrdinalIgnoreCase))
            : null;

        target ??= available
            .Where(r => string.Equals(r.Channel, effectiveChannel, StringComparison.OrdinalIgnoreCase))
            .MaxBy(ParseVersion);

        // La cible ne doit JAMAIS être sous le plancher : sinon le client se met à jour vers une version
        // qui reste en dessous du minimum, se force à nouveau, et boucle indéfiniment.
        if (minimum is not null && (target is null || ParseVersion(target) < ParseVersion(minimum)))
        {
            target = minimum;
        }

        if (target is null) return UpdateManifest.None;

        return new UpdateManifest(
            LatestVersion: target.Version,
            MinimumVersion: minimum?.Version,
            DownloadUrl: target.DownloadUrl,
            Sha256: target.Sha256,
            Signature: target.Signature,
            ReleaseNotes: target.Notes);
    }
}
