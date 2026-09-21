namespace Sage100Mcp.Hosting;

/// <summary>Transports disponibles pour le serveur MCP.</summary>
public enum McpTransport
{
    /// <summary>Mode historique : le client MCP lance le process, JSON-RPC sur stdin/stdout.</summary>
    Stdio,

    /// <summary>Streamable HTTP : un service pour N clients, éventuellement distants.</summary>
    Http
}

/// <summary>
/// Choix du transport avant toute construction d'hôte (le type d'hôte en dépend).
/// Priorité : arguments de ligne de commande, puis variable d'environnement, puis stdio.
/// </summary>
public static class McpTransportSelection
{
    public const string EnvironmentVariable = "MCP_TRANSPORT";

    public static McpTransport Resolve(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (IsFlag(arg, "--http")) return McpTransport.Http;
            if (IsFlag(arg, "--stdio")) return McpTransport.Stdio;

            if (arg.StartsWith("--transport", StringComparison.OrdinalIgnoreCase))
            {
                var separator = arg.IndexOf('=');
                var value = separator >= 0
                    ? arg[(separator + 1)..]
                    : (i + 1 < args.Length ? args[i + 1] : null);
                if (TryParse(value, out var fromArgs)) return fromArgs;
            }
        }

        return TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var fromEnvironment)
            ? fromEnvironment
            : McpTransport.Stdio;
    }

    /// <summary>
    /// Retire nos propres arguments avant de les passer à l'hôte : le fournisseur de
    /// configuration en ligne de commande interprète <c>--http</c> comme une clé et
    /// consommerait l'argument suivant (typiquement <c>--urls</c>) comme sa valeur.
    /// </summary>
    public static string[] StripTransportArgs(string[] args)
    {
        var kept = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (IsFlag(arg, "--http") || IsFlag(arg, "--stdio"))
                continue;

            if (arg.StartsWith("--transport", StringComparison.OrdinalIgnoreCase))
            {
                // Forme "--transport http" : l'argument suivant est la valeur, à retirer aussi.
                if (!arg.Contains('=') && i + 1 < args.Length && TryParse(args[i + 1], out _))
                    i++;
                continue;
            }

            kept.Add(arg);
        }

        return kept.ToArray();
    }

    private static bool IsFlag(string arg, string flag) =>
        string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string? value, out McpTransport transport)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "http":
            case "streamable-http":
                transport = McpTransport.Http;
                return true;
            case "stdio":
                transport = McpTransport.Stdio;
                return true;
            default:
                transport = McpTransport.Stdio;
                return false;
        }
    }
}
