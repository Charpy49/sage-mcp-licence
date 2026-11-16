using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage100Mcp.Installer;

/// <summary>
/// Déclaration du lanceur comme serveur MCP local dans <c>claude_desktop_config.json</c>.
/// <para>
/// Claude Desktop existe en deux éditions sous Windows, qui ne lisent pas le même fichier :
/// l'installation classique lit <c>%APPDATA%\Claude</c>, l'application du Store (MSIX) lit sa copie
/// virtualisée <c>%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\Claude</c>. On écrit dans
/// toutes celles qui existent.
/// </para>
/// </summary>
internal static class ClaudeDesktopConfig
{
    private const string FileName = "claude_desktop_config.json";

    public static IReadOnlyList<string> FindConfigFiles()
    {
        var dirs = new List<string>();

        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                var dir = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (Directory.Exists(dir)) dirs.Add(dir);
            }
        }

        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (Directory.Exists(classic) || dirs.Count == 0) dirs.Add(classic);

        return dirs.Select(d => Path.Combine(d, FileName)).ToList();
    }

    /// <summary>Nom sous lequel ce lanceur est déjà déclaré, pour reproposer le même.</summary>
    public static string? FindRegisteredName(string launcherPath)
    {
        foreach (var file in FindConfigFiles().Where(File.Exists))
        {
            try
            {
                if (Load(file)["mcpServers"] is not JsonObject servers) continue;
                foreach (var (name, node) in servers)
                {
                    if (SamePath(node?["command"]?.GetValue<string>(), launcherPath)) return name;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
                // Fichier illisible : on le signalera à l'écriture.
            }
        }
        return null;
    }

    /// <summary>Dossier d'installation déjà déclaré dans Claude Desktop, s'il y en a un.</summary>
    public static string? FindInstalledLauncherDir()
    {
        foreach (var file in FindConfigFiles().Where(File.Exists))
        {
            try
            {
                if (Load(file)["mcpServers"] is not JsonObject servers) continue;
                foreach (var (_, node) in servers)
                {
                    var command = node?["command"]?.GetValue<string>();
                    if (command is not null
                        && string.Equals(Path.GetFileName(command), Installation.LauncherFileName, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(command))
                    {
                        return Path.GetDirectoryName(command);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Ajoute ou met à jour l'entrée du serveur. Une entrée qui pointe déjà sur ce lanceur sous un
    /// autre nom est renommée plutôt que dupliquée : deux entrées lanceraient deux serveurs.
    /// </summary>
    /// <returns>Fichiers modifiés.</returns>
    public static IReadOnlyList<string> Register(string serverName, string launcherPath)
    {
        var written = new List<string>();
        foreach (var file in FindConfigFiles())
        {
            RegisterIn(file, serverName, launcherPath);
            written.Add(file);
        }
        return written;
    }

    /// <summary>Écrit l'entrée du serveur dans un fichier de configuration donné.</summary>
    internal static void RegisterIn(string file, string serverName, string launcherPath)
    {
        JsonObject root;
        if (File.Exists(file))
        {
            try
            {
                root = Load(file);
            }
            catch (JsonException ex)
            {
                // Surtout ne pas écraser les préférences de l'utilisateur avec un fichier neuf.
                throw new InvalidDataException($"{file} est illisible ({ex.Message}) : corrigez-le puis relancez.", ex);
            }
            File.Copy(file, $"{file}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        foreach (var stale in servers
                     .Where(s => s.Key != serverName && SamePath(s.Value?["command"]?.GetValue<string>(), launcherPath))
                     .Select(s => s.Key)
                     .ToList())
        {
            servers.Remove(stale);
        }

        // Les autres propriétés éventuelles de l'entrée (env…) sont conservées.
        if (servers[serverName] is not JsonObject entry)
        {
            entry = new JsonObject();
            servers[serverName] = entry;
        }
        entry["command"] = launcherPath;

        var temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    private static JsonObject Load(string file) =>
        JsonNode.Parse(File.ReadAllText(file),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? new JsonObject();

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
