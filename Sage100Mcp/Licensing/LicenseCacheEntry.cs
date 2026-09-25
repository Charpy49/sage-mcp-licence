namespace Sage100Mcp.Licensing;

/// <summary>
/// Dernier jeton de licence signé reçu, conservé pour couvrir les pannes réseau courtes. Le fichier n'a
/// pas besoin d'être protégé : modifié, sa signature ne correspond plus ; copié sur un autre poste, son
/// empreinte de poste ne correspond plus. Un cache au format antérieur à la 1.3.0 se lit sans jeton et
/// est simplement ignoré.
/// </summary>
public sealed class LicenseCacheEntry
{
    public string? Token { get; init; }
    public string? TokenSignature { get; init; }
}
