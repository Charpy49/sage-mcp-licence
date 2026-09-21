using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sage100Mcp.Hosting;

namespace Sage100Mcp.Licensing;

public sealed record LicenseValidationResult(bool Success, string? ClientName, IReadOnlyList<string>? AllowedTools,
    string? ErrorMessage, UpdateInfo? Update = null)
{
    public static LicenseValidationResult Ok(string clientName, IReadOnlyList<string>? allowedTools,
        UpdateInfo? update = null) =>
        new(true, clientName, allowedTools, null, update);

    public static LicenseValidationResult Fail(string errorMessage) =>
        new(false, null, null, errorMessage);
}

/// <summary>
/// Manifeste de mise à jour renvoyé par le serveur de licences. <see cref="LatestVersion"/> est la
/// version que <b>ce</b> poste doit viser (elle tient déjà compte de son canal et de son épinglage).
/// </summary>
public sealed record UpdateInfo(
    string? LatestVersion,
    string? MinimumVersion,
    string? DownloadUrl,
    string? Sha256,
    string? Signature,
    string? ReleaseNotes)
{
    /// <summary>Une version plus récente que <paramref name="installedVersion"/> est disponible.</summary>
    public bool IsUpdateAvailable(string installedVersion) =>
        Compare(LatestVersion, installedVersion) > 0;

    /// <summary>
    /// La version installée est sous le plancher : la mise à jour doit être appliquée de façon
    /// bloquante (correctif de sécurité ou rupture de protocole).
    /// </summary>
    public bool IsUpdateRequired(string installedVersion) =>
        Compare(MinimumVersion, installedVersion) > 0;

    /// <summary>&gt; 0 si <paramref name="candidate"/> est plus récent que <paramref name="installed"/>.</summary>
    private static int Compare(string? candidate, string installed)
    {
        if (!Version.TryParse(candidate, out var target)) return 0;
        if (!Version.TryParse(installed, out var current)) return 0;
        return target.CompareTo(current);
    }
}

public static class LicenseClient
{
    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sage100Mcp", "license-cache.json");

    /// <param name="transport">"stdio" ou "http", remonté au serveur de licences pour l'inventaire.</param>
    public static async Task<LicenseValidationResult> ValidateAsync(LicenseOptions options, string transport,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ServerUrl) || string.IsNullOrWhiteSpace(options.LicenseKey))
        {
            return LicenseValidationResult.Fail(
                "Configuration de licence manquante : renseignez License:ServerUrl et License:LicenseKey " +
                "(appsettings.json ou variables d'environnement License__ServerUrl / License__LicenseKey).");
        }

        var keyHash = HashKey(options.LicenseKey);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
            var requestUri = $"{options.ServerUrl.TrimEnd('/')}/api/license/validate";
            using var response = await http.PostAsJsonAsync(requestUri, new
            {
                licenseKey = options.LicenseKey,
                installedVersion = AppVersion.Current,
                transport
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                return FallBackToCache(options, keyHash,
                    $"Le serveur de licences a répondu {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<ValidateResponseDto>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

            if (payload is null)
            {
                return FallBackToCache(options, keyHash, "Réponse du serveur de licences illisible.");
            }

            if (!payload.Valid)
            {
                return LicenseValidationResult.Fail(DescribeRejection(payload));
            }

            var clientName = payload.ClientName ?? "client";
            WriteCache(new LicenseCacheEntry
            {
                LicenseKeyHash = keyHash,
                ClientName = clientName,
                ExpiresAtUtc = payload.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(1),
                AllowedTools = payload.AllowedTools,
                ValidatedAtUtc = DateTimeOffset.UtcNow,
            });

            return LicenseValidationResult.Ok(clientName, payload.AllowedTools, payload.Update);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FallBackToCache(options, keyHash, $"Serveur de licences injoignable ({ex.Message}).");
        }
    }

    private static LicenseValidationResult FallBackToCache(LicenseOptions options, string keyHash, string reason)
    {
        var cache = ReadCache();
        if (cache is null || cache.LicenseKeyHash != keyHash)
        {
            return LicenseValidationResult.Fail(
                $"{reason} Aucune validation de licence récente en cache pour démarrer en mode dégradé.");
        }

        var graceDeadline = cache.ValidatedAtUtc.AddDays(options.GracePeriodDays);
        if (DateTimeOffset.UtcNow > graceDeadline)
        {
            return LicenseValidationResult.Fail(
                $"{reason} La dernière validation en cache date de plus de {options.GracePeriodDays} jour(s) " +
                "(période de tolérance dépassée) : reconnectez ce poste au serveur de licences.");
        }

        if (DateTimeOffset.UtcNow > cache.ExpiresAtUtc)
        {
            return LicenseValidationResult.Fail($"{reason} La licence en cache est expirée.");
        }

        Console.Error.WriteLine(
            $"[Licence] {reason} Démarrage en mode dégradé avec la dernière validation connue " +
            $"({cache.ValidatedAtUtc:u}, tolérance {options.GracePeriodDays} jour(s)).");

        return LicenseValidationResult.Ok(cache.ClientName, cache.AllowedTools);
    }

    private static string DescribeRejection(ValidateResponseDto payload) => payload.Reason switch
    {
        "unknown_key" => "Clé de licence inconnue du serveur de licences.",
        "revoked" => $"Licence révoquée pour {payload.ClientName ?? "ce client"}.",
        "expired" => $"Licence expirée le {payload.ExpiresAtUtc:d} pour {payload.ClientName ?? "ce client"}.",
        "missing_key" => "Aucune clé de licence transmise.",
        _ => $"Licence refusée par le serveur ({payload.Reason ?? "raison inconnue"}).",
    };

    private static string HashKey(string licenseKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(licenseKey.Trim().ToUpperInvariant())));

    private static LicenseCacheEntry? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return null;
            return JsonSerializer.Deserialize<LicenseCacheEntry>(File.ReadAllText(CacheFilePath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(LicenseCacheEntry entry)
    {
        try
        {
            var dir = Path.GetDirectoryName(CacheFilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(entry));
        }
        catch
        {
            // Le cache est une optimisation de robustesse ; son échec ne doit pas empêcher le démarrage.
        }
    }

    private sealed class ValidateResponseDto
    {
        public bool Valid { get; set; }
        public string? Reason { get; set; }
        public string? ClientName { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public IReadOnlyList<string>? AllowedTools { get; set; }

        /// <summary>Absent des réponses des serveurs de licences antérieurs à la gestion des mises à jour.</summary>
        public UpdateInfo? Update { get; set; }
    }
}
