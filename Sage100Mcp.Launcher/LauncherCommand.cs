namespace Sage100Mcp.Launcher;

/// <summary>Commandes propres au shim, invoquées à la main par un opérateur.</summary>
internal enum LauncherCommand
{
    /// <summary>Comportement normal : lancer le serveur MCP.</summary>
    Launch,

    /// <summary>Afficher l'état de l'installation.</summary>
    Status,

    /// <summary>Revenir à la version antérieure conservée.</summary>
    Rollback,

    /// <summary>Forcer la mise à jour maintenant, sans lancer le serveur ensuite.</summary>
    Update
}

internal static class LauncherCommands
{
    /// <summary>
    /// Extrait la commande du shim et renvoie les arguments restants, transmis tels quels au
    /// serveur (<c>--http</c>, <c>--urls</c>…). Le préfixe <c>--launcher-</c> évite toute collision
    /// avec les arguments du serveur.
    /// </summary>
    public static LauncherCommand Parse(string[] args, out string[] serverArgs)
    {
        var command = LauncherCommand.Launch;
        var kept = new List<string>(args.Length);

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--launcher-status":
                    command = LauncherCommand.Status;
                    break;
                case "--launcher-rollback":
                    command = LauncherCommand.Rollback;
                    break;
                case "--launcher-update":
                    command = LauncherCommand.Update;
                    break;
                default:
                    kept.Add(arg);
                    break;
            }
        }

        serverArgs = kept.ToArray();
        return command;
    }
}
