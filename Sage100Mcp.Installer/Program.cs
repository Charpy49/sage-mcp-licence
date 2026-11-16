namespace Sage100Mcp.Installer;

// Installateur du serveur MCP Sage 100 : copie le lanceur, écrit appsettings.json à partir des
// fichiers .gcm / .mae du client, télécharge le serveur et le déclare dans Claude Desktop.
// Aucun droit d'administration n'est nécessaire : tout se passe dans le profil de l'utilisateur.
internal static class Program
{
    // Les boîtes de dialogue de fichiers exigent un thread STA, que les instructions de niveau
    // supérieur ne permettent pas de déclarer.
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
    }
}
