using System.IO.Compression;
using System.Security.Cryptography;

namespace Sage100Mcp.Launcher;

/// <summary>
/// Téléchargement, vérification et installation d'un paquet de version.
/// Rien n'est activé avant que tout ait été vérifié : le dossier de destination définitif
/// n'apparaît qu'à la dernière étape, déjà complet et marqué comme tel.
/// </summary>
internal static class PackageInstaller
{
    public static async Task<bool> TryInstallAsync(
        VersionStore store, ReleaseManifest manifest, LauncherConfig config, CancellationToken ct)
    {
        if (!manifest.HasDownload)
        {
            Log.Warn("Manifeste incomplet (version, URL ou SHA-256 manquant) : installation abandonnée.");
            return false;
        }

        var version = manifest.LatestVersion!;
        Directory.CreateDirectory(store.VersionsDir);

        var packagePath = Path.Combine(store.VersionsDir, $"{version}.download-{Guid.NewGuid():N}.zip");
        var stagingDir = Path.Combine(store.VersionsDir, $"{version}.staging-{Guid.NewGuid():N}");

        try
        {
            Log.Info($"Téléchargement de la version {version}…");
            if (!await TryFetchAsync(manifest.DownloadUrl!, packagePath, config, ct)) return false;

            var actualHash = await ComputeSha256Async(packagePath, ct);
            if (!actualHash.Equals(manifest.Sha256!.Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"Empreinte SHA-256 du paquet {version} incorrecte (attendu {manifest.Sha256}, " +
                          $"obtenu {actualHash}) : paquet corrompu ou altéré, installation abandonnée.");
                return false;
            }

            // ExtractToDirectory refuse les entrées qui sortent du dossier de destination (zip slip).
            ZipFile.ExtractToDirectory(packagePath, stagingDir);

            var serverExe = Path.Combine(stagingDir, VersionStore.ServerExeName);
            if (!File.Exists(serverExe))
            {
                Log.Error($"Le paquet {version} ne contient pas {VersionStore.ServerExeName} à sa racine : " +
                          "installation abandonnée.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(config.RequirePublisher))
            {
                if (!Authenticode.TryVerify(serverExe, config.RequirePublisher, out var signatureError))
                {
                    Log.Error($"Signature de la version {version} refusée : {signatureError}. Installation abandonnée.");
                    return false;
                }
                Log.Info($"Signature de la version {version} vérifiée ({config.RequirePublisher}).");
            }
            else
            {
                Log.Warn("Update:RequirePublisher n'est pas configuré : la version installée n'est pas " +
                         "authentifiée (le SHA-256 ne protège pas d'un point de téléchargement compromis).");
            }

            store.MarkComplete(stagingDir);

            var target = store.VersionDir(version);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(stagingDir, target);

            Log.Info($"Version {version} installée.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error($"Installation de la version {version} échouée : {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteFile(packagePath);
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>
    /// Récupère le paquet. Les chemins locaux et UNC sont acceptés en plus de http(s) : chez un
    /// client, déposer le paquet sur un partage réseau évite d'exposer un point de téléchargement.
    /// </summary>
    private static async Task<bool> TryFetchAsync(string source, string destination, LauncherConfig config,
        CancellationToken ct)
    {
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(config.DownloadTimeoutMinutes) };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Error($"Téléchargement refusé ({(int)response.StatusCode}) : {source}");
                    return false;
                }

                await using var target = File.Create(destination);
                await response.Content.CopyToAsync(target, ct);
                return true;
            }

            // Chemin local ou UNC (\\serveur\partage\paquet.zip), ou URI file://
            var localPath = uri?.Scheme == Uri.UriSchemeFile ? uri.LocalPath : source;
            if (!File.Exists(localPath))
            {
                Log.Error($"Paquet introuvable : {localPath}");
                return false;
            }

            File.Copy(localPath, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error($"Téléchargement échoué depuis {source} : {ex.Message}");
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
