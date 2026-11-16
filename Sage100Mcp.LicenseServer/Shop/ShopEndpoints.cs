using System.Threading.RateLimiting;
using Stripe;
using CheckoutSession = Stripe.Checkout.Session;

namespace Sage100Mcp.LicenseServer.Shop;

public sealed record CheckoutRequest(string Plan, string? Email);
public sealed record PortalRequest(string LicenseKey);
public sealed record RedirectResponse(string Url);

/// <summary>
/// Endpoints publics du site commercial (<c>wwwroot/index.html</c>) et webhook Stripe.
/// </summary>
public static class ShopEndpoints
{
    public const string RateLimitPolicy = "shop";

    /// <summary>Limite par adresse IP : création de sessions Checkout, essais et accès au portail.</summary>
    public static IServiceCollection AddShop(this IServiceCollection services)
    {
        services.AddSingleton<SubscriptionRepository>();
        services.AddSingleton<ShopService>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "inconnu",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    public static void MapShop(this WebApplication app)
    {
        var shop = app.MapGroup("/api/shop").RequireRateLimiting(RateLimitPolicy);

        // Catalogue : le site affiche les offres réellement en vente (prix Stripe configuré).
        shop.MapGet("/plans", (ShopService service) => Results.Ok(new
        {
            available = service.IsConfigured,
            trialDays = Plans.TrialDays,
            plans = Plans.All.Select(p => new { p.Code, p.Name, p.MonthlyPriceEur }),
        }));

        shop.MapPost("/checkout", async (CheckoutRequest request, ShopService service, HttpContext http,
            ILogger<ShopService> logger, CancellationToken ct) =>
        {
            if (!service.IsConfigured) return Unavailable();
            try
            {
                var (url, error) = await service.CreateCheckoutAsync(request.Plan, request.Email, BaseUrl(http), ct);
                return error is not null
                    ? Results.BadRequest(new { error })
                    : Results.Ok(new RedirectResponse(url!));
            }
            catch (StripeException ex)
            {
                logger.LogError(ex, "Création de la session Checkout ({Plan}) refusée par Stripe.", request.Plan);
                return Results.Json(new { error = "Le paiement est momentanément indisponible. Réessayez dans un instant." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Page de confirmation : renvoie la clé de licence de la session (status = ready / pending / expired).
        shop.MapGet("/session/{sessionId}", async (string sessionId, ShopService service,
            ILogger<ShopService> logger, CancellationToken ct) =>
        {
            if (!service.IsConfigured) return Unavailable();
            if (!sessionId.StartsWith("cs_", StringComparison.Ordinal) || sessionId.Length > 255)
                return Results.NotFound();
            try
            {
                var result = await service.GetCheckoutResultAsync(sessionId, ct);
                return Results.Ok(new { result, installerUrl = service.InstallerUrl });
            }
            catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
            {
                return Results.NotFound();
            }
            catch (StripeException ex)
            {
                logger.LogError(ex, "Lecture de la session {Session} impossible.", sessionId);
                return Results.Json(new { error = "Stripe ne répond pas. Rechargez la page dans un instant." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        shop.MapPost("/portal", async (PortalRequest request, ShopService service, HttpContext http,
            ILogger<ShopService> logger, CancellationToken ct) =>
        {
            if (!service.IsConfigured) return Unavailable();
            if (string.IsNullOrWhiteSpace(request.LicenseKey) || request.LicenseKey.Length > 100)
                return Results.BadRequest(new { error = "Saisissez votre clé de licence." });
            try
            {
                var url = await service.CreatePortalUrlAsync(request.LicenseKey.Trim(), BaseUrl(http), ct);
                return url is null
                    ? Results.BadRequest(new { error = "Aucun abonnement en ligne n'est associé à cette clé." })
                    : Results.Ok(new RedirectResponse(url));
            }
            catch (StripeException ex)
            {
                logger.LogError(ex, "Ouverture du portail client refusée par Stripe.");
                return Results.Json(new { error = "L'espace client est momentanément indisponible." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Webhook Stripe : la signature (Stripe-Signature, secret Stripe:WebhookSecret) est la seule
        // authentification. Une erreur renvoie 500 pour que Stripe réessaie (jusqu'à 3 jours).
        app.MapPost("/api/stripe/webhook", async (HttpRequest request, ShopService service,
            ILogger<ShopService> logger, CancellationToken ct) =>
        {
            if (!service.IsConfigured || string.IsNullOrEmpty(service.WebhookSecret)) return Unavailable();

            var json = await new StreamReader(request.Body).ReadToEndAsync(ct);
            Event stripeEvent;
            try
            {
                // Pas d'échec sur un écart de version d'API : seuls les identifiants sont lus dans
                // l'événement, l'état est relu chez Stripe avec la version de la bibliothèque.
                stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"],
                    service.WebhookSecret, throwOnApiVersionMismatch: false);
            }
            catch (StripeException)
            {
                return Results.BadRequest();
            }

            switch (stripeEvent.Data.Object)
            {
                case CheckoutSession session when stripeEvent.Type == EventTypes.CheckoutSessionCompleted:
                    await service.ProvisionAsync(session.Id, ct);
                    break;
                case Subscription subscription when stripeEvent.Type.StartsWith("customer.subscription.", StringComparison.Ordinal):
                    await service.SyncSubscriptionAsync(subscription.Id, ct);
                    break;
                default:
                    logger.LogDebug("Événement Stripe ignoré : {Type}.", stripeEvent.Type);
                    break;
            }
            return Results.Ok();
        });
    }

    private static IResult Unavailable() =>
        Results.Json(new { error = "La boutique en ligne n'est pas encore ouverte." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// URL publique du site, pour les retours de Stripe. <c>Shop:PublicBaseUrl</c> si défini (domaine
    /// personnalisé), sinon l'hôte de la requête — toujours en https : derrière le frontal d'App Service,
    /// l'application reçoit la requête en http.
    /// </summary>
    private static string BaseUrl(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>()["Shop:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
        var scheme = http.Request.Host.Host is "localhost" or "127.0.0.1" ? http.Request.Scheme : "https";
        return $"{scheme}://{http.Request.Host}";
    }
}
