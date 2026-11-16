using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Sage100Mcp.Installer;

/// <summary>Étapes d'installation hors interface : runtimes, copie du lanceur, premier téléchargement.</summary>
internal static class Installation
{
    public const string LauncherFileName = "Sage100Mcp.Launcher.exe";

    /// <summary>
    /// Dossier par défaut : dans le profil de l'utilisateur, sans droits d'administration. Le lanceur
    /// doit pouvoir écrire à côté de lui (versions\, current.txt), ce qu'interdit Program Files.
    /// </summary>
    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sage100Mcp");

    /// <summary>
    /// Runtimes requis. Le lanceur et le serveur sont publiés « dépendants du framework » ; le serveur
    /// référence en plus ASP.NET Core (transport HTTP), requis même en stdio.
    /// </summary>
    public static IReadOnlyList<string> MissingRuntimes()
    {
        var shared = Path.Combine(DotnetRoot(), "shared");
        var missing = new List<string>();
        foreach (var (framework, label) in new[]
                 {
                     ("Microsoft.NETCore.App", ".NET Runtime 10"),
                     ("Microsoft.AspNetCore.App", "ASP.NET Core Runtime 10")
                 })
        {
            var dir = Path.Combine(shared, framework);
            var found = Directory.Exists(dir)
                        && Directory.EnumerateDirectories(dir)
                            .Select(Path.GetFileName)
                            .Any(v => v is not null && v.StartsWith("10.", StringComparison.Ordinal));
            if (!found) missing.Add(label);
        }
        return missing;
    }

    private static string DotnetRoot()
    {
        // Emplacement lu par l'hôte .NET lui-même pour les applications dépendantes du framework.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            if (key?.GetValue("InstallLocation") is string location && Directory.Exists(location)) return location;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    /// <summary>Copie le lanceur embarqué. Ne réécrit pas un fichier identique (il peut être en cours d'exécution).</summary>
    /// <returns>Vrai si le fichier a été écrit, faux s'il était déjà à jour.</returns>
    public static bool CopyLauncher(string installDir)
    {
        Directory.CreateDirectory(installDir);
        var target = Path.Combine(installDir, LauncherFileName);

        using var resource = typeof(Installation).Assembly.GetManifestResourceStream(LauncherFileName)
                             ?? throw new InvalidOperationException("Lanceur absent de l'installateur (génération incomplète).");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();

        if (File.Exists(target) && SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            return false;
        }

        try
        {
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Impossible de remplacer {target} : il est probablement en cours d'utilisation. " +
                "Quittez complètement Claude Desktop (icône de la zone de notification comprise) puis relancez l'installation.", ex);
        }
        return true;
    }

    /// <summary>
    /// Télécharge et active la version du serveur MCP autorisée par la licence
    /// (<c>--launcher-update</c>). Valide au passage l'adresse du serveur et la clé de licence.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunLauncherUpdateAsync(string installDir, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(installDir, LauncherFileName),
            WorkingDirectory = installDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Le lanceur écrit stderr en UTF-8 dès qu'il est redirigé.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--launcher-update");

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Le lanceur n'a pas pu être démarré.");

        var output = new StringBuilder();
        void Append(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
        }
        process.OutputDataReceived += Append;
        process.ErrorDataReceived += Append;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Démarrage à froid du serveur de licences (~15 s) + téléchargement du paquet.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            lock (output) output.AppendLine("Délai dépassé : téléchargement interrompu.");
            return (-1, output.ToString());
        }

        process.WaitForExit(); // vide les derniers événements de lecture asynchrone
        lock (output) return (process.ExitCode, output.ToString());
    }
}
