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
