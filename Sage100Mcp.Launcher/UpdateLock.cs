namespace Sage100Mcp.Launcher;

/// <summary>
/// Verrou inter-processus des installations : plusieurs clients MCP (Claude Desktop, Claude Code)
/// lancent chacun leur shim, et deux téléchargements simultanés se marcheraient dessus.
/// <para>
/// Un fichier verrou plutôt qu'un <see cref="Mutex"/> nommé, pour deux raisons : un mutex Win32
/// doit être libéré par le thread qui l'a acquis, ce qui est incompatible avec un <c>await</c>
/// (la continuation reprend sur un autre thread du pool) ; et un fichier ouvert en
/// <see cref="FileShare.None"/> est libéré par le système d'exploitation si le processus meurt.
/// </para>
/// </summary>
internal sealed class UpdateLock : IDisposable
{
    private const string FileName = "update.lock";

    private readonly FileStream _stream;

    private UpdateLock(FileStream stream) => _stream = stream;

    /// <summary>Renvoie null si un autre processus détient le verrou au-delà du délai imparti.</summary>
    public static async Task<UpdateLock?> TryAcquireAsync(string rootDir, TimeSpan timeout, CancellationToken ct)
    {
        var path = Path.Combine(rootDir, FileName);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            try
            {
                return new UpdateLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose));
            }
            catch (IOException)
            {
                // Verrou détenu ailleurs.
                if (DateTimeOffset.UtcNow >= deadline) return null;
                await Task.Delay(200, ct);
            }
            catch (UnauthorizedAccessException)
            {
                // Droits insuffisants sur la racine d'installation : inutile d'insister.
                return null;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // Rien de récupérable ici ; le fichier sera libéré à la fin du processus.
        }
    }
}
