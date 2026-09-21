using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage100Mcp.Launcher;

/// <summary>
/// Manifeste renvoyé par le serveur de licences. <see cref="LatestVersion"/> est la version que
/// <b>ce</b> poste doit viser : elle tient déjà compte de son canal et de son éventuel épinglage.
/// </summary>
internal sealed record ReleaseManifest
{
    public string? LatestVersion { get; init; }
    public string? MinimumVersion { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Sha256 { get; init; }

    /// <summary>
    /// Signature détachée du manifeste. <b>Non vérifiée à ce jour</b> : le schéma de signature n'est
    /// pas arrêté. L'authenticité repose sur <see cref="LauncherConfig.RequirePublisher"/>, qui
    /// vérifie la signature Authenticode du binaire téléchargé.
    /// </summary>
    public string? Signature { get; init; }

    public string? ReleaseNotes { get; init; }

    public bool HasDownload =>
        !string.IsNullOrWhiteSpace(LatestVersion) &&
        !string.IsNullOrWhiteSpace(DownloadUrl) &&
        !string.IsNullOrWhiteSpace(Sha256);
}

internal static class UpdateClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Interroge <c>/api/version/check</c>. Cet endpoint est distinct de la validation de licence :
    /// le shim n'a besoin que du manifeste, et une licence expirée doit tout de même pouvoir
    /// recevoir un correctif. Renvoie null en cas d'échec — une panne réseau ne doit jamais
    /// empêcher le serveur déjà installé de démarrer.
    /// </summary>
    public static async Task<ReleaseManifest?> CheckAsync(
        LauncherConfig config, string installedVersion, string transport, TimeSpan timeout, CancellationToken ct)
    {
        if (!config.CanCheckForUpdates) return null;

        try
        {
            using var http = new HttpClient { Timeout = timeout };
            var uri = $"{config.ServerUrl!.TrimEnd('/')}/api/version/check";

            using var response = await http.PostAsJsonAsync(uri, new
            {
                licenseKey = config.LicenseKey,
                installedVersion,
                transport
            }, Json, ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Vérification de mise à jour refusée par le serveur ({(int)response.StatusCode}).");
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ReleaseManifest>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn($"Serveur de mise à jour injoignable ({ex.Message}).");
            return null;
        }
    }
}
