using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage100Mcp.Installer;

/// <summary>
/// Lecture et écriture du <c>appsettings.json</c> de la racine d'installation, partagé par le lanceur
/// et le serveur MCP.
/// <para>
/// Le fichier est <b>fusionné</b>, jamais régénéré : seuls la licence et la liste des bases sont
/// réécrites. Tout le reste (section <c>Mcp:Http</c>, réglages <c>Update</c> ajoutés à la main…)
/// survit à une réinstallation.
/// </para>
/// </summary>
internal sealed class InstallSettings
{
    public const string FileName = "appsettings.json";
    public const string DefaultServerUrl = "https://sage-mcp-licences.azurewebsites.net/";

    public string ServerUrl { get; set; } = DefaultServerUrl;
    public string LicenseKey { get; set; } = "";
    public List<SageDatabase> Databases { get; } = [];

    private static readonly JsonDocumentOptions ReadOptions =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Les accents des descriptions restent lisibles dans le fichier au lieu de é.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Charge la configuration existante d'un dossier, ou null s'il n'y en a pas.</summary>
    public static InstallSettings? TryLoad(string installDir)
    {
        var path = Path.Combine(installDir, FileName);
        if (!File.Exists(path)) return null;

        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"{path} ne contient pas un objet JSON.");

        var settings = new InstallSettings
        {
            ServerUrl = root["License"]?["ServerUrl"]?.GetValue<string>() is { Length: > 0 } url ? url : DefaultServerUrl,
            LicenseKey = root["License"]?["LicenseKey"]?.GetValue<string>() ?? ""
        };

        if (root["Sage"]?["Databases"] is JsonArray databases)
        {
            foreach (var node in databases.OfType<JsonObject>())
            {
                settings.Databases.Add(SageDatabase.FromConnectionString(
                    node["Name"]?.GetValue<string>() ?? "",
                    node["Description"]?.GetValue<string>() ?? "",
                    node["ConnectionString"]?.GetValue<string>() ?? "",
                    node["Default"]?.GetValue<bool>() ?? false));
            }
        }

        return settings;
    }

    /// <summary>
    /// Écrit la configuration. L'ancien fichier est sauvegardé à côté : il contient des chaînes de
    /// connexion qu'on ne doit pas pouvoir perdre sur une fausse manœuvre.
    /// </summary>
    /// <returns>Chemin de la sauvegarde, ou null s'il n'y avait pas de fichier.</returns>
    public string? Save(string installDir)
    {
        var path = Path.Combine(installDir, FileName);
        JsonObject root;
        string? backup = null;

        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"{path} ne contient pas un objet JSON.");
            backup = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backup, overwrite: true);
        }
        else
        {
            root = NewTemplate();
        }

        var license = Section(root, "License");
        license["ServerUrl"] = ServerUrl.Trim();
        license["LicenseKey"] = LicenseKey.Trim();
        license["TimeoutSeconds"] ??= 30;
        license["GracePeriodDays"] ??= 7;

        var databases = new JsonArray();
        foreach (var db in Databases)
        {
            databases.Add(new JsonObject
            {
                ["Name"] = db.Name.Trim(),
                ["Description"] = db.Description.Trim(),
                ["ConnectionString"] = db.BuildConnectionString(),
                ["Default"] = db.Default
            });
        }
        Section(root, "Sage")["Databases"] = databases;

        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return backup;
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section) return section;
        section = new JsonObject();
        root[name] = section;
        return section;
    }

    /// <summary>Même modèle que celui empaqueté par publish-release.ps1.</summary>
    private static JsonObject NewTemplate() => new()
    {
        ["License"] = new JsonObject(),
        ["Update"] = new JsonObject { ["Enabled"] = true, ["CheckIntervalHours"] = 4, ["RequirePublisher"] = "" },
        ["Mcp"] = new JsonObject
        {
            ["Http"] = new JsonObject
            {
                ["Urls"] = "http://127.0.0.1:5099",
                ["EndpointPath"] = "/mcp",
                ["ApiKeys"] = new JsonArray(),
                ["AllowedOrigins"] = new JsonArray()
            }
        },
        ["Sage"] = new JsonObject()
    };
}
