using System.Diagnostics;
using Sage100Mcp.Launcher;

// Shim de lancement du serveur MCP Sage 100.
//
// Il résout la version à exécuter, lance le serveur en lui transmettant ses propres flux standard,
// et met à jour l'installation en tâche de fond. Trois principes le gouvernent :
//
//  1. Ne jamais toucher à stdout : le serveur hérite directement des handles du shim, qui n'écrit
//     donc pas un octet dans le canal JSON-RPC. Ses messages partent sur stderr.
//  2. Ne jamais rallonger le démarrage : la mise à jour est téléchargée pendant que le serveur
//     tourne et ne prend effet qu'au lancement suivant. Seuls deux cas bloquent : aucune version
//     installée, et version sous le plancher imposé par l'éditeur.
//  3. Ne jamais empêcher le serveur de démarrer : toute panne du canal de mise à jour (réseau,
//     licence, signature) se traduit par un avertissement, pas par un échec.

Log.UseUtf8WhenRedirected();

var rootDir = AppContext.BaseDirectory;
var store = new VersionStore(rootDir);

// Une configuration illisible ne doit pas priver le client de son serveur MCP : on repart sur des
// valeurs par défaut, sans mise à jour automatique, et on lance quand même la version installée.
LauncherConfig config;
try
{
    config = LauncherConfig.Load(rootDir);
}
catch (Exception ex)
{
    Log.Error($"Lecture de la configuration impossible ({ex.Message}) : mise à jour désactivée.");
    config = LauncherConfig.Disabled;
}

// Arguments propres au shim, retirés avant transmission au serveur.
var command = LauncherCommands.Parse(args, out var serverArgs);

if (command == LauncherCommand.Status) return ShowStatus(store, config);
if (command == LauncherCommand.Rollback) return Rollback(store);

var transport = serverArgs.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase))
                || string.Equals(Environment.GetEnvironmentVariable("MCP_TRANSPORT"), "http", StringComparison.OrdinalIgnoreCase)
    ? "http"
    : "stdio";

var launchVersion = store.ResolveLaunchVersion();

// --- Interrogation du serveur de licences ---------------------------------------------------
// Une seule fois par fenêtre (4 h par défaut) pour ne pas payer un aller-retour réseau à chaque
// lancement, sauf s'il n'y a rien à lancer ou si l'opérateur force la mise à jour.
ReleaseManifest? manifest = null;
var forcedCheck = command == LauncherCommand.Update;

if (config.CanCheckForUpdates && (forcedCheck || launchVersion is null || store.ShouldCheck(config.CheckIntervalHours)))
{
    var timeout = forcedCheck || launchVersion is null
        ? TimeSpan.FromMinutes(1)                              // rien à lancer : on peut attendre
        : TimeSpan.FromSeconds(config.CheckTimeoutSeconds);    // sinon ce délai s'ajoute au démarrage

    manifest = await UpdateClient.CheckAsync(config, launchVersion ?? "0.0.0", transport, timeout, CancellationToken.None);
    store.RecordCheck();
}

// --- Cas bloquants --------------------------------------------------------------------------
if (launchVersion is null)
{
    if (manifest is null)
    {
        Log.Error("Aucune version du serveur n'est installée et le serveur de mise à jour est " +
                  "injoignable. Vérifiez License:ServerUrl et License:LicenseKey dans appsettings.json, " +
                  $"ou déposez une version dans {store.VersionsDir}.");
        return 1;
    }

    if (!await InstallExclusiveAsync(store, manifest, config))
    {
        Log.Error("Installation initiale impossible : le serveur MCP ne peut pas démarrer.");
        return 1;
    }

    launchVersion = manifest.LatestVersion!;
    store.SetCurrent(launchVersion);
    BootstrapConfiguration(store, launchVersion);
}
else if (manifest is not null && VersionStore.IsNewer(manifest.MinimumVersion, launchVersion))
{
    // Mise à jour obligatoire : version installée sous le plancher (correctif de sécurité,
    // rupture de protocole). Le serveur ne démarre pas avant.
    Log.Warn($"Version {launchVersion} sous le plancher requis {manifest.MinimumVersion} : " +
             "mise à jour obligatoire avant démarrage.");

    if (await InstallExclusiveAsync(store, manifest, config))
    {
        launchVersion = manifest.LatestVersion!;
        store.SetCurrent(launchVersion);
    }
    else
    {
        // Démarrer une version périmée reste préférable à ne pas démarrer du tout : c'est à
        // l'éditeur de révoquer la licence s'il veut réellement bloquer ce poste.
        Log.Warn("Mise à jour obligatoire échouée : démarrage avec la version installée.");
    }
}

if (command == LauncherCommand.Update)
{
    // Commande explicite de l'opérateur : on n'enchaîne pas sur le lancement du serveur.
    if (manifest is not null && VersionStore.IsNewer(manifest.LatestVersion, launchVersion))
    {
        if (await InstallExclusiveAsync(store, manifest, config))
        {
            store.SetCurrent(manifest.LatestVersion!);
            store.Prune(config.KeepVersions, manifest.LatestVersion);
            Console.Out.WriteLine($"Version active : {manifest.LatestVersion}");
            return 0;
        }
        return 1;
    }

    Console.Out.WriteLine($"Déjà à jour (version active : {launchVersion}).");
    return 0;
}

// --- Lancement du serveur -------------------------------------------------------------------
var exePath = store.ServerExePath(launchVersion!);
var startInfo = new ProcessStartInfo
{
    FileName = exePath,
    WorkingDirectory = rootDir,
    // Pas de redirection : le serveur hérite des handles du shim. C'est ce qui garantit qu'aucune
    // copie de flux ne peut corrompre ni retarder le JSON-RPC.
    UseShellExecute = false,
};
foreach (var arg in serverArgs) startInfo.ArgumentList.Add(arg);

// La configuration du client (bases Sage, licence) vit à la racine, jamais dans les dossiers de
// version : sinon chaque mise à jour écraserait les chaînes de connexion du client.
startInfo.Environment["SAGE100MCP_CONFIG_DIR"] = rootDir;

Process? server;
try
{
    server = Process.Start(startInfo);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
{
    Log.Error($"Impossible de démarrer {exePath} : {ex.Message}");
    return 1;
}

if (server is null)
{
    Log.Error($"Impossible de démarrer {exePath}.");
    return 1;
}

// --- Mise à jour en tâche de fond -----------------------------------------------------------
// Elle s'exécute pendant que le serveur travaille et ne prend effet qu'au lancement suivant.
//
// La version est activée (current.txt) dès la fin de l'installation, pas à l'arrêt du serveur :
// les clients MCP tuent le shim au lieu d'attendre sa sortie, et le code qui suit
// WaitForExitAsync ne s'exécute alors jamais — l'installation restait indéfiniment inactive.
// Basculer pendant que le serveur tourne est sans risque : il vit dans son propre dossier de version.
Task<bool>? backgroundInstall = null;
if (manifest is not null && VersionStore.IsNewer(manifest.LatestVersion, launchVersion))
{
    Log.Info($"Version {manifest.LatestVersion} disponible : installation en arrière-plan, " +
             "active au prochain démarrage.");
    backgroundInstall = InstallAndActivateAsync(store, manifest, config);
}

await server.WaitForExitAsync();

if (backgroundInstall is not null)
{
    // Le serveur est arrêté : on accorde un dernier délai au téléchargement, sans plus, pour ne
    // pas laisser le client MCP attendre la fin d'un processus qui n'a plus rien à faire.
    // Seul le ménage des anciennes versions attend l'arrêt (la version qui tournait ne peut pas
    // être supprimée avant) ; s'il est sauté parce que le shim est tué, il sera refait plus tard.
    // Un échec ici ne doit pas masquer le code de sortie du serveur.
    try
    {
        var completed = await Task.WhenAny(backgroundInstall,
            Task.Delay(TimeSpan.FromSeconds(config.PostExitGraceSeconds)));

        if (completed == backgroundInstall && await backgroundInstall)
        {
            store.Prune(config.KeepVersions, store.ReadCurrent());
        }
        else if (completed != backgroundInstall)
        {
            Log.Info("Installation encore en cours à l'arrêt du serveur : reprise au prochain lancement.");
        }
    }
    catch (Exception ex)
    {
        Log.Error($"Nettoyage des anciennes versions impossible : {ex.Message}");
    }
}

return server.ExitCode;

// ---------------------------------------------------------------------------------------------

// Installe une version sous verrou inter-processus. Ne lève jamais : l'appelant doit pouvoir
// enchaîner sur le lancement du serveur quoi qu'il arrive.
static async Task<bool> InstallExclusiveAsync(VersionStore store, ReleaseManifest manifest, LauncherConfig config)
{
    try
    {
        using var updateLock = await UpdateLock.TryAcquireAsync(store.RootDir, TimeSpan.FromSeconds(5),
            CancellationToken.None);

        if (updateLock is null)
        {
            Log.Info("Une autre instance installe déjà une mise à jour : abandon.");
            return false;
        }

        store.CleanIncompleteInstalls();

        // Peut-être installée entre-temps par l'instance qui détenait le verrou.
        if (manifest.LatestVersion is { } version && store.IsComplete(version))
        {
            Log.Info($"Version {version} déjà installée.");
            return true;
        }

        return await PackageInstaller.TryInstallAsync(store, manifest, config, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Log.Error($"Mise à jour interrompue : {ex.Message}");
        return false;
    }
}

// Installe une version en arrière-plan puis l'active aussitôt pour les lancements suivants.
// Ne lève jamais, comme InstallExclusiveAsync.
static async Task<bool> InstallAndActivateAsync(VersionStore store, ReleaseManifest manifest, LauncherConfig config)
{
    if (!await InstallExclusiveAsync(store, manifest, config)) return false;

    var version = manifest.LatestVersion!;
    try
    {
        // Une autre instance a pu activer entre-temps une version plus récente : ne pas la rétrograder.
        if (store.ReadCurrent() is { } current && !VersionStore.IsNewer(version, current)) return true;

        store.SetCurrent(version);
        Log.Info($"Version {version} activée pour le prochain démarrage.");
        return true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Log.Error($"Activation de la version {version} impossible : {ex.Message}");
        return false;
    }
}

// Première installation : le paquet embarque un appsettings.json d'exemple, on le remonte à la
// racine s'il n'y en a pas encore. Un fichier existant n'est jamais écrasé — il contient les
// chaînes de connexion du client.
static void BootstrapConfiguration(VersionStore store, string version)
{
    var rootConfig = Path.Combine(store.RootDir, "appsettings.json");
    if (File.Exists(rootConfig)) return;

    var packaged = Path.Combine(store.VersionDir(version), "appsettings.json");
    if (!File.Exists(packaged)) return;

    try
    {
        File.Copy(packaged, rootConfig);
        Log.Info($"Configuration initiale créée : {rootConfig}. Renseignez-y les bases Sage.");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Log.Warn($"Copie de la configuration initiale impossible ({ex.Message}).");
    }
}

// Les deux commandes ci-dessous sont invoquées à la main par un opérateur : elles n'ont pas de
// serveur MCP en aval, donc écrire sur stdout y est sans danger.

static int ShowStatus(VersionStore store, LauncherConfig config)
{
    var installed = store.InstalledVersions();
    Console.Out.WriteLine($"Racine            : {store.RootDir}");
    Console.Out.WriteLine($"Version active    : {store.ResolveLaunchVersion() ?? "(aucune)"}");
    Console.Out.WriteLine($"Versions installées : {(installed.Count == 0 ? "(aucune)" : string.Join(", ", installed))}");
    Console.Out.WriteLine($"Mise à jour       : {(config.Enabled ? "activée" : "désactivée")}");
    Console.Out.WriteLine($"Serveur           : {config.ServerUrl ?? "(non configuré)"}");
    Console.Out.WriteLine($"Licence           : {(string.IsNullOrWhiteSpace(config.LicenseKey) ? "(non configurée)" : "configurée")}");
    Console.Out.WriteLine($"Publicateur exigé : {config.RequirePublisher ?? "(aucun — versions non authentifiées)"}");
    return 0;
}

static int Rollback(VersionStore store)
{
    var active = store.ResolveLaunchVersion();
    if (active is null)
    {
        Console.Error.WriteLine("Aucune version active.");
        return 1;
    }

    var previous = store.PreviousVersion(active);
    if (previous is null)
    {
        Console.Error.WriteLine($"Aucune version antérieure à {active} n'est conservée.");
        return 1;
    }

    store.SetCurrent(previous);
    Console.Out.WriteLine($"Retour arrière : {active} → {previous}. Effectif au prochain démarrage.");
    return 0;
}

