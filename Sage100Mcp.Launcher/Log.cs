using System.Text;

namespace Sage100Mcp.Launcher;

/// <summary>
/// Journalisation du shim.
/// <para>
/// ⚠️ <b>Rien ne doit jamais partir sur stdout.</b> Le shim transmet ses propres flux standard au
/// serveur MCP par héritage de handles : stdout porte le JSON-RPC du protocole, et un seul octet
/// parasite le corrompt. stderr est en revanche récupéré et affiché par les clients MCP.
/// </para>
/// </summary>
internal static class Log
{
    /// <summary>
    /// Écrit stderr et stdout en UTF-8 quand ils sont redirigés (client MCP, installateur). Par défaut .NET encode avec
    /// la page de code de la console (850/1252), que les clients MCP relisent en UTF-8 : les accents
    /// arrivaient illisibles. Sur une vraie console (commandes --status, --rollback), on ne touche
    /// à rien. Sans BOM : le préambule serait émis tel quel en tête du flux.
    /// </summary>
    public static void UseUtf8WhenRedirected()
    {
        try
        {
            var utf8 = new UTF8Encoding(false);
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });

            // stdout n'est écrit que par les commandes --launcher-* (lues par l'installateur) ; en
            // lancement normal le shim n'y écrit rien, le serveur en hérite directement.
            if (Console.IsOutputRedirected)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        }
        catch
        {
            // Accents dégradés plutôt que shim bloqué.
        }
    }

    public static void Info(string message) => Write(message);

    public static void Warn(string message) => Write($"[!] {message}");

    public static void Error(string message) => Write($"[ERREUR] {message}");

    private static void Write(string message)
    {
        try
        {
            Console.Error.WriteLine($"[Launcher] {message}");
        }
        catch
        {
            // Un stderr fermé ou saturé ne doit jamais empêcher le serveur de démarrer.
        }
    }
}
