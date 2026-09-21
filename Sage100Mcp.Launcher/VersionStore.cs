namespace Sage100Mcp.Launcher;

/// <summary>
/// Arborescence d'installation. Les versions vivent côte à côte, ce qui évite le seul vrai obstacle
/// technique : Windows verrouille l'exécutable en cours, donc un programme ne peut pas se remplacer
/// lui-même. La nouvelle version s'installe dans un dossier neuf et prend effet au lancement suivant.
/// <code>
/// racine\
///   Sage100Mcp.Launcher.exe   ← ce shim, point d'entrée stable des clients MCP
///   appsettings.json          ← configuration du CLIENT (bases Sage, licence) — jamais versionnée
///   current.txt               ← version active
///   last-check.txt            ← horodatage de la dernière interrogation du serveur
///   versions\1.1.0\Sage100Mcp.exe
///   versions\1.2.0\Sage100Mcp.exe
/// </code>
/// </summary>
internal sealed class VersionStore
{
    public const string ServerExeName = "Sage100Mcp.exe";

    /// <summary>
    /// Marqueur écrit à la toute fin d'une installation. Un dossier sans ce fichier est une
    /// extraction interrompue : il ne doit jamais être activé.
    /// </summary>
    private const string CompleteMarker = ".complete";

    public VersionStore(string rootDir)
    {
        RootDir = rootDir;
        VersionsDir = Path.Combine(rootDir, "versions");
    }

    public string RootDir { get; }
    public string VersionsDir { get; }
    public string CurrentFile => Path.Combine(RootDir, "current.txt");
    public string LastCheckFile => Path.Combine(RootDir, "last-check.txt");

    public string VersionDir(string version) => Path.Combine(VersionsDir, version);

    public string ServerExePath(string version) => Path.Combine(VersionDir(version), ServerExeName);

    public bool IsComplete(string version) =>
        File.Exists(Path.Combine(VersionDir(version), CompleteMarker)) && File.Exists(ServerExePath(version));

    public void MarkComplete(string versionDir) =>
        File.WriteAllText(Path.Combine(versionDir, CompleteMarker), DateTimeOffset.UtcNow.ToString("O"));

    public string? ReadCurrent()
    {
        try
        {
            if (!File.Exists(CurrentFile)) return null;
            var value = File.ReadAllText(CurrentFile).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bascule la version active. L'écriture passe par un fichier temporaire puis un déplacement
    /// avec remplacement : un shim qui lit <c>current.txt</c> au même instant voit l'ancienne ou la
    /// nouvelle valeur, jamais un fichier tronqué.
    /// </summary>
    public void SetCurrent(string version)
    {
        var temp = CurrentFile + ".tmp";
        File.WriteAllText(temp, version);
        File.Move(temp, CurrentFile, overwrite: true);
    }

    /// <summary>Versions installées et complètes, de la plus récente à la plus ancienne.</summary>
    public IReadOnlyList<string> InstalledVersions()
    {
        if (!Directory.Exists(VersionsDir)) return [];

        return Directory.EnumerateDirectories(VersionsDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsComplete)
            .OrderByDescending(Parse)
            .ToList();
    }

    /// <summary>
    /// Version à lancer : celle de <c>current.txt</c> si elle est utilisable, sinon la plus récente
    /// installée (cas d'un <c>current.txt</c> corrompu ou d'un dossier supprimé à la main).
    /// </summary>
    public string? ResolveLaunchVersion()
    {
        if (ReadCurrent() is { } current && IsComplete(current)) return current;

        var fallback = InstalledVersions().FirstOrDefault();
        if (fallback is not null && ReadCurrent() is not null)
        {
            Log.Warn($"La version active déclarée est inutilisable : repli sur {fallback}.");
        }
        return fallback;
    }

    /// <summary>Version installée juste en dessous de l'active, cible d'un retour arrière.</summary>
    public string? PreviousVersion(string activeVersion) =>
        InstalledVersions().FirstOrDefault(v => Parse(v) < Parse(activeVersion));

    public bool ShouldCheck(int intervalHours)
    {
        try
        {
            if (!File.Exists(LastCheckFile)) return true;
            if (!DateTimeOffset.TryParse(File.ReadAllText(LastCheckFile).Trim(), out var last)) return true;
            return DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(intervalHours);
        }
        catch (IOException)
        {
            return true;
        }
    }

    public void RecordCheck()
    {
        try
        {
            File.WriteAllText(LastCheckFile, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // Sans horodatage on interrogera le serveur au prochain lancement : sans gravité.
        }
    }

    /// <summary>
    /// Supprime les versions les plus anciennes en gardant <paramref name="keep"/> versions,
    /// dont toujours l'active — même si elle n'est pas la plus récente (cas d'un retour arrière).
    /// </summary>
    public void Prune(int keep, string? activeVersion)
    {
        var installed = InstalledVersions();
        if (installed.Count <= keep) return;

        foreach (var version in installed.Skip(keep))
        {
            if (version == activeVersion) continue;
            try
            {
                Directory.Delete(VersionDir(version), recursive: true);
                Log.Info($"Version {version} supprimée (au-delà des {keep} conservées).");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Version encore en cours d'exécution par un autre client MCP : on réessaiera.
            }
        }
    }

    /// <summary>Nettoie les extractions interrompues laissées par un lancement précédent.</summary>
    public void CleanIncompleteInstalls()
    {
        if (!Directory.Exists(VersionsDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(VersionsDir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || IsComplete(name)) continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                Log.Info($"Installation incomplète nettoyée : {name}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Peut-être une installation en cours dans un autre processus : on laisse.
            }
        }
    }

    public static Version Parse(string? version) =>
        Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0, 0);

    /// <summary>Vrai si <paramref name="candidate"/> est strictement plus récent que <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? candidate, string? installed) =>
        Version.TryParse(candidate, out var target) && Parse(installed) < target;
}
