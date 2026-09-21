namespace Sage100Mcp.Configuration;

/// <summary>
/// Configuration du transport HTTP (section "Mcp:Http" de appsettings.json).
/// Ignorée en transport stdio.
/// </summary>
public sealed class McpHttpOptions
{
    public const string SectionName = "Mcp:Http";

    /// <summary>Écoute locale par défaut : rien n'est exposé au réseau sans configuration explicite.</summary>
    public const string DefaultUrls = "http://127.0.0.1:5099";

    /// <summary>
    /// Adresses d'écoute, séparées par ';' (ex. "http://0.0.0.0:5099").
    /// <c>--urls</c> et <c>ASPNETCORE_URLS</c> restent prioritaires.
    /// </summary>
    public string? Urls { get; set; }

    /// <summary>Chemin de l'endpoint MCP (URL complète = &lt;url&gt;&lt;EndpointPath&gt;).</summary>
    public string EndpointPath { get; set; } = "/mcp";

    /// <summary>
    /// Clés d'API acceptées, en en-tête <c>Authorization: Bearer &lt;clé&gt;</c> ou <c>X-Api-Key</c>.
    /// Vide ⇒ accès anonyme, autorisé <b>uniquement</b> en écoute sur la boucle locale.
    /// </summary>
    public List<string> ApiKeys { get; set; } = new();

    /// <summary>
    /// Origines web autorisées (protection anti-DNS rebinding). Les clients MCP natifs
    /// n'envoient pas d'en-tête <c>Origin</c> et ne sont donc pas concernés ; une requête
    /// avec un <c>Origin</c> hors de cette liste est refusée.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();
}
