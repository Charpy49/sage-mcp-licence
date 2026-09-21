using System.Text.Json;

namespace Sage100Mcp.Launcher;

/// <summary>
/// Configuration du shim, lue dans le <c>appsettings.json</c> <b>de la racine d'installation</b> —
/// le même fichier que celui du serveur MCP, ce qui évite de dupliquer la clé de licence.
/// Les variables d'environnement <c>Section__Cle</c> sont prioritaires, comme côté serveur.
/// </summary>
internal sealed class LauncherConfig
{
    /// <summary>URL du serveur de licences, qui sert aussi de canal de diffusion des versions.</summary>
    public string? ServerUrl { get; private init; }

    public string? LicenseKey { get; private init; }

    /// <summary>Mise à jour automatique. À false, le shim se contente de lancer la version installée.</summary>
    public bool Enabled { get; private init; } = true;

    /// <summary>Intervalle minimal entre deux interrogations du serveur (heures).</summary>
    public int CheckIntervalHours { get; private init; } = 4;

    /// <summary>
    /// Délai maximal de l'interrogation faite <b>avant</b> le lancement. Volontairement court : il
    /// s'ajoute au démarrage du serveur MCP, que le client MCP attend.
    /// </summary>
    public int CheckTimeoutSeconds { get; private init; } = 3;

    public int DownloadTimeoutMinutes { get; private init; } = 10;

    /// <summary>Nombre de versions conservées (jamais moins de 2 : il faut de quoi revenir en arrière).</summary>
    public int KeepVersions { get; private init; } = 3;

    /// <summary>
    /// Délai accordé à un téléchargement encore en cours quand le serveur MCP s'arrête. Au-delà,
    /// le shim abandonne : faire attendre le client MCP après la fin de session serait pire.
    /// </summary>
    public int PostExitGraceSeconds { get; private init; } = 10;

    /// <summary>
    /// Sujet attendu du certificat de signature (ex. "CN=Ma Société"). Renseigné, aucune version
    /// non signée par ce publicateur n'est installée. Vide, la chaîne de mise à jour ne repose que
    /// sur HTTPS et le SHA-256 — ce qui ne protège pas d'une compromission du point de téléchargement.
    /// </summary>
    public string? RequirePublisher { get; private init; }

    /// <summary>Repli quand la configuration est illisible : on lance sans jamais tenter de mise à jour.</summary>
    public static LauncherConfig Disabled { get; } = new() { Enabled = false };

    public bool CanCheckForUpdates =>
        Enabled && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(LicenseKey);

    public static LauncherConfig Load(string rootDir)
    {
        // Les valeurs sont extraites tant que le JsonDocument est vivant : un JsonElement conservé
        // au-delà du "using" pointe sur un document libéré.
        var file = ReadSettings(Path.Combine(rootDir, "appsettings.json"));

        string? Setting(string section, string key) =>
            Env($"{section}__{key}") ?? (file.TryGetValue($"{section}:{key}", out var value) ? value : null);

        return new LauncherConfig
        {
            ServerUrl = Setting("License", "ServerUrl"),
            LicenseKey = Setting("License", "LicenseKey"),
            Enabled = Bool(Setting("Update", "Enabled"), true),
            CheckIntervalHours = Int(Setting("Update", "CheckIntervalHours"), 4),
            CheckTimeoutSeconds = Int(Setting("Update", "CheckTimeoutSeconds"), 3),
            DownloadTimeoutMinutes = Int(Setting("Update", "DownloadTimeoutMinutes"), 10),
            KeepVersions = Math.Max(2, Int(Setting("Update", "KeepVersions"), 3)),
            PostExitGraceSeconds = Int(Setting("Update", "PostExitGraceSeconds"), 10),
            RequirePublisher = Setting("Update", "RequirePublisher"),
        };
    }

    /// <summary>Aplatit les sections utiles de appsettings.json en paires "Section:Clé" → valeur.</summary>
    private static Dictionary<string, string?> ReadSettings(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (document.RootElement.ValueKind != JsonValueKind.Object) return values;

            foreach (var sectionName in new[] { "License", "Update" })
            {
                if (!document.RootElement.TryGetProperty(sectionName, out var section)) continue;
                if (section.ValueKind != JsonValueKind.Object) continue;

                foreach (var property in section.EnumerateObject())
                {
                    if (Scalar(property.Value) is { } text)
                    {
                        values[$"{sectionName}:{property.Name}"] = text;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{path} illisible ({ex.Message}) : valeurs par défaut, pas de mise à jour automatique.");
        }

        return values;
    }

    private static string? Scalar(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool Bool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int Int(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
