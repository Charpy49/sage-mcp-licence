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

        MachineIdentity machine;
        try
        {
            machine = MachineIdentity.Current();
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail($"Impossible d'identifier ce poste pour la licence : {ex.Message}");
        }

        // Nonce : le jeton renvoyé doit le reprendre, ce qui interdit de rejouer en ligne un ancien jeton.
        var nonce = RandomNumberGenerator.GetHexString(32);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
            var requestUri = $"{options.ServerUrl.TrimEnd('/')}/api/license/validate";
            using var response = await http.PostAsJsonAsync(requestUri, new
            {
                licenseKey = options.LicenseKey,
                installedVersion = AppVersion.Current,
                transport,
                machineId = machine.Id,
                machineName = machine.Name,
                nonce
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                return FallBackToCache(options, keyHash, machine,
                    $"Le serveur de licences a répondu {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<ValidateResponseDto>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

            if (payload is null)
            {
                return FallBackToCache(options, keyHash, machine, "Réponse du serveur de licences illisible.");
            }

            if (!payload.Valid)
            {
                return LicenseValidationResult.Fail(DescribeRejection(payload));
            }

            // Une réponse « valide » ne vaut rien sans jeton signé qui la confirme pour ce poste et cette requête.
            var token = LicenseToken.Verify(payload.Token, payload.TokenSignature);
            if (token is null)
            {
                return LicenseValidationResult.Fail(payload.Token is null
                    ? "Le serveur de licences n'a pas renvoyé de jeton signé (serveur non à jour ou usurpé)."
                    : "Jeton de licence à la signature invalide : vérifiez License:ServerUrl.");
            }
            if (token.LicenseKeyHash != keyHash || token.MachineId != machine.Id || token.Nonce != nonce)
            {
                return LicenseValidationResult.Fail(
                    "Jeton de licence émis pour une autre clé, un autre poste ou une autre requête : démarrage refusé.");
            }

            WriteCache(new LicenseCacheEntry { Token = payload.Token, TokenSignature = payload.TokenSignature });

            return LicenseValidationResult.Ok(token.ClientName, token.AllowedTools, payload.Update);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FallBackToCache(options, keyHash, machine, $"Serveur de licences injoignable ({ex.Message}).");
        }
    }

    /// <summary>Tolérance sur l'horloge du poste, en retard sur celle du serveur de licences.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    private static LicenseValidationResult FallBackToCache(LicenseOptions options, string keyHash,
        MachineIdentity machine, string reason)
    {
        var cache = ReadCache();
        var token = LicenseToken.Verify(cache?.Token, cache?.TokenSignature);
        if (token is null || token.LicenseKeyHash != keyHash)
        {
            return LicenseValidationResult.Fail(
                $"{reason} Aucune validation de licence récente en cache pour démarrer en mode dégradé.");
        }

        if (token.MachineId != machine.Id)
        {
            return LicenseValidationResult.Fail(
                $"{reason} Le cache de licence a été émis pour un autre poste : reconnectez ce poste au serveur de licences.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < token.IssuedAtUtc - ClockSkew)
        {
            return LicenseValidationResult.Fail(
                $"{reason} L'horloge de ce poste est antérieure à la dernière validation ({token.IssuedAtUtc:u}) : " +
                "corrigez la date système.");
        }

        // Le plus strict des deux : la tolérance fixée par le serveur (signée) et celle de la configuration locale.
        var graceDeadline = token.IssuedAtUtc.AddDays(options.GracePeriodDays);
        if (token.OfflineUntilUtc < graceDeadline) graceDeadline = token.OfflineUntilUtc;
        if (now > graceDeadline)
        {
            return LicenseValidationResult.Fail(
                $"{reason} La dernière validation en cache date du {token.IssuedAtUtc:u} " +
                "(période de tolérance dépassée) : reconnectez ce poste au serveur de licences.");
        }

        if (now > token.ExpiresAtUtc)
        {
            return LicenseValidationResult.Fail($"{reason} La licence en cache est expirée.");
        }

        Console.Error.WriteLine(
            $"[Licence] {reason} Démarrage en mode dégradé avec la dernière validation connue " +
            $"({token.IssuedAtUtc:u}, acceptée jusqu'au {graceDeadline:u}).");

        return LicenseValidationResult.Ok(token.ClientName, token.AllowedTools);
    }

    private static string DescribeRejection(ValidateResponseDto payload) => payload.Reason switch
    {
        "unknown_key" => "Clé de licence inconnue du serveur de licences.",
        "revoked" => $"Licence révoquée pour {payload.ClientName ?? "ce client"}.",
        "expired" => $"Licence expirée le {payload.ExpiresAtUtc:d} pour {payload.ClientName ?? "ce client"}.",
        "missing_key" => "Aucune clé de licence transmise.",
        "machine_limit" =>
            $"Licence de {payload.ClientName ?? "ce client"} déjà activée sur {payload.MaxMachines ?? 1} poste(s) " +
            $"({string.Join(", ", payload.ActivatedMachines ?? [])}) : ce poste ({Environment.MachineName}) n'est pas autorisé. " +
            "Demandez à votre fournisseur de libérer un poste ou d'étendre la licence.",
        "missing_machine_id" => "Le serveur de licences exige l'identification du poste : mettez à jour le serveur MCP.",
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

        /// <summary>Jeton signé (voir <see cref="LicenseToken"/>) ; seule source de vérité sur la licence.</summary>
        public string? Token { get; set; }
        public string? TokenSignature { get; set; }

        /// <summary>Renseignés sur un refus <c>machine_limit</c>.</summary>
        public IReadOnlyList<string>? ActivatedMachines { get; set; }
        public int? MaxMachines { get; set; }
    }
}
