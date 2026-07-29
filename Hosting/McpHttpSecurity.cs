using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sage100Mcp.Configuration;

namespace Sage100Mcp.Hosting;

/// <summary>
/// Garde-fous du transport HTTP. En stdio, le serveur hérite de l'identité Windows de
/// l'utilisateur qui l'a lancé ; en HTTP, il n'y a plus rien d'implicite : sans clé d'API,
/// n'importe qui sur le réseau lirait le chiffre d'affaires, les impayés et la trésorerie.
/// </summary>
public static class McpHttpSecurity
{
    /// <summary>Endpoint de supervision, volontairement accessible sans clé (ne divulgue rien).</summary>
    public const string HealthPath = "/health";

    /// <summary>
    /// Refuse le démarrage si le serveur écoute au-delà de la boucle locale sans clé d'API
    /// configurée (fail closed).
    /// </summary>
    public static void EnsureSafeConfiguration(McpHttpOptions options, string urls, ILogger logger)
    {
        var exposed = SplitUrls(urls).Where(IsReachableFromNetwork).ToArray();
        var hasApiKey = options.ApiKeys.Any(k => !string.IsNullOrWhiteSpace(k));

        if (exposed.Length > 0 && !hasApiKey)
        {
            throw new InvalidOperationException(
                $"Refus de démarrer : écoute sur {string.Join(", ", exposed)} (donc accessible depuis le réseau) " +
                "sans aucune clé d'API. Renseignez Mcp:Http:ApiKeys dans appsettings.json (ou la variable " +
                "d'environnement Mcp__Http__ApiKeys__0). Pour un usage strictement local, écoutez sur " +
                $"{McpHttpOptions.DefaultUrls}.");
        }

        if (!hasApiKey)
        {
            logger.LogWarning(
                "Serveur MCP HTTP sans clé d'API : accès anonyme, limité à cette machine ({Urls}).", urls);
        }
    }

    /// <summary>
    /// Contrôle d'accès : validation de l'en-tête <c>Origin</c> (anti-DNS rebinding) puis
    /// de la clé d'API. À brancher avant <c>MapMcp</c>.
    /// </summary>
    public static IApplicationBuilder UseMcpHttpSecurity(this IApplicationBuilder app, McpHttpOptions options)
    {
        var apiKeys = options.ApiKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Encoding.UTF8.GetBytes(k.Trim()))
            .ToArray();

        var allowedOrigins = options.AllowedOrigins
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .ToArray();

        return app.Use(async (context, next) =>
        {
            // Un navigateur envoie toujours Origin ; les clients MCP natifs, jamais.
            var origin = context.Request.Headers["Origin"].ToString();
            if (!string.IsNullOrEmpty(origin) &&
                !allowedOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "origin_refusee",
                    "Origine non autorisée. Ajoutez-la à Mcp:Http:AllowedOrigins si l'appel est légitime.");
                return;
            }

            var isHealthCheck = context.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase);
            if (apiKeys.Length > 0 && !isHealthCheck && !HasValidApiKey(context, apiKeys))
            {
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "cle_api_invalide",
                    "Clé d'API absente ou invalide (en-tête Authorization: Bearer <clé> ou X-Api-Key).");
                return;
            }

            await next();
        });
    }

    private static bool HasValidApiKey(HttpContext context, byte[][] apiKeys)
    {
        var presented = ExtractApiKey(context);
        if (string.IsNullOrEmpty(presented)) return false;

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var valid = false;

        // Pas de court-circuit : le temps de réponse ne doit pas révéler la position de la clé.
        foreach (var key in apiKeys)
        {
            if (key.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(key, presentedBytes))
                valid = true;
        }

        return valid;
    }

    private static string? ExtractApiKey(HttpContext context)
    {
        var authorization = context.Request.Headers["Authorization"].ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        var apiKeyHeader = context.Request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrWhiteSpace(apiKeyHeader) ? null : apiKeyHeader.Trim();
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { erreur = code, message });
    }

    public static string[] SplitUrls(string urls) =>
        urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Vrai dès que l'adresse d'écoute n'est pas restreinte à la boucle locale
    /// (donc y compris les jokers <c>0.0.0.0</c>, <c>+</c> et <c>*</c>).
    /// </summary>
    private static bool IsReachableFromNetwork(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : url;

        return host.Trim('[', ']') is not ("localhost" or "127.0.0.1" or "::1");
    }
}
