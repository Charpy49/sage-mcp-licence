using Microsoft.AspNetCore.DataProtection;
using Sage100Mcp.LicenseServer.Licensing;
using Stripe;
using CheckoutSession = Stripe.Checkout.Session;
using CheckoutSessionService = Stripe.Checkout.SessionService;
using PortalSessionService = Stripe.BillingPortal.SessionService;

namespace Sage100Mcp.LicenseServer.Shop;

/// <summary>
/// Vente en ligne : session Stripe Checkout → licence. Stripe est la source de vérité de l'abonnement ;
/// la licence n'en est qu'un reflet (outils autorisés, échéance), recalculé à chaque événement.
/// <para>
/// Les événements Stripe peuvent arriver en double ou dans le désordre : on ne lit jamais l'état dans
/// l'événement lui-même, on relit l'abonnement chez Stripe puis on applique son état courant. Le
/// provisionnement est idempotent (une licence par session Checkout), et un verrou unique sérialise le
/// tout — la page de confirmation et le webhook arrivent souvent à la même seconde. Un verrou en mémoire
/// suffit : la base SQLite impose de toute façon une instance unique.
/// </para>
/// </summary>
public sealed class ShopService
{
    private readonly StripeClient? _stripe;
    private readonly IConfiguration _configuration;
    private readonly LicenseRepository _licenses;
    private readonly SubscriptionRepository _subscriptions;
    private readonly IDataProtector _protector;
    private readonly ILogger<ShopService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ShopService(IConfiguration configuration, LicenseRepository licenses, SubscriptionRepository subscriptions,
        IDataProtectionProvider dataProtection, ILogger<ShopService> logger)
    {
        _configuration = configuration;
        _licenses = licenses;
        _subscriptions = subscriptions;
        _protector = dataProtection.CreateProtector("Shop.LicenseKey");
        _logger = logger;

        var secretKey = configuration["Stripe:SecretKey"];
        _stripe = string.IsNullOrWhiteSpace(secretKey) ? null : new StripeClient(secretKey);
    }

    /// <summary>
    /// Faux tant que la clé secrète Stripe n'est pas configurée : la boutique répond alors 503, mais le
    /// serveur démarre quand même — la validation des licences existantes ne doit pas en dépendre.
    /// </summary>
    public bool IsConfigured => _stripe is not null;

    public string? WebhookSecret => _configuration["Stripe:WebhookSecret"];

    private int GraceDays => _configuration.GetValue("Shop:GraceDays", 3);
    private int KeyRevealDays => _configuration.GetValue("Shop:KeyRevealDays", 7);
    public string? InstallerUrl => _configuration["Shop:InstallerUrl"] is { Length: > 0 } url ? url : null;

    private string? PriceIdFor(string planCode) =>
        _configuration[$"Stripe:Prices:{planCode}"] is { Length: > 0 } id ? id : null;

    private Plan? PlanForPrice(string? priceId) =>
        priceId is null ? null : Plans.All.FirstOrDefault(p => PriceIdFor(p.Code) == priceId);

    // --- Achat ---------------------------------------------------------------------------------

    /// <summary>
    /// Crée la session Checkout d'une offre. L'essai s'appuie sur le prix de l'offre Production avec
    /// 30 jours offerts et sans carte bancaire : à la fin de l'essai, l'abonnement est annulé faute de
    /// moyen de paiement — sauf si le client en a ajouté un entre-temps depuis le portail.
    /// </summary>
    /// <returns>L'URL Stripe vers laquelle rediriger le navigateur, ou un message d'erreur pour l'utilisateur.</returns>
    public async Task<(string? Url, string? Error)> CreateCheckoutAsync(string planCode, string? email,
        string baseUrl, CancellationToken ct)
    {
        var plan = Plans.Find(planCode);
        if (plan is null) return (null, "Offre inconnue.");

        var isTrial = plan.Code == Plans.Trial;
        var priceId = PriceIdFor(isTrial ? Plans.Production : plan.Code);
        if (priceId is null)
        {
            _logger.LogError("Aucun prix Stripe configuré pour l'offre {Plan} (Stripe:Prices).", plan.Code);
            return (null, "Cette offre n'est pas encore disponible à la vente.");
        }

        email = email?.Trim();
        if (isTrial)
        {
            if (string.IsNullOrEmpty(email) || email.Length > 254 || !email.Contains('@'))
                return (null, "Indiquez une adresse e-mail valide pour démarrer l'essai.");
            if (await _subscriptions.HasTrialAsync(email, ct))
                return (null, "Un essai gratuit a déjà été accordé à cette adresse. Choisissez une offre pour continuer.");
        }

        var metadata = new Dictionary<string, string> { ["plan"] = plan.Code };
        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = [new Stripe.Checkout.SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            // {CHECKOUT_SESSION_ID} est remplacé par Stripe : la page de confirmation s'en sert pour
            // récupérer la clé de licence.
            SuccessUrl = $"{baseUrl}/merci.html?session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = $"{baseUrl}/#offres",
            Locale = "fr",
            CustomerEmail = string.IsNullOrEmpty(email) ? null : email,
            BillingAddressCollection = "required",
            NameCollection = new Stripe.Checkout.SessionNameCollectionOptions
            {
                Business = new Stripe.Checkout.SessionNameCollectionBusinessOptions { Enabled = true },
            },
            TaxIdCollection = new Stripe.Checkout.SessionTaxIdCollectionOptions { Enabled = true },
            Metadata = metadata,
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions { Metadata = metadata },
        };

        if (isTrial)
        {
            options.PaymentMethodCollection = "if_required";
            options.SubscriptionData.TrialPeriodDays = Plans.TrialDays;
            options.SubscriptionData.TrialSettings = new Stripe.Checkout.SessionSubscriptionDataTrialSettingsOptions
            {
                EndBehavior = new Stripe.Checkout.SessionSubscriptionDataTrialSettingsEndBehaviorOptions
                {
                    MissingPaymentMethod = "cancel",
                },
            };
        }
        else
        {
            options.AllowPromotionCodes = true;
        }

        var session = await new CheckoutSessionService(_stripe!).CreateAsync(options, cancellationToken: ct);
        return (session.Url, null);
    }

    // --- Provisionnement -----------------------------------------------------------------------

    /// <summary>Résultat présenté sur la page de confirmation.</summary>
    public sealed record CheckoutResult(string Status, string? PlanName = null, string? ClientName = null,
        string? Email = null, string? LicenseKey = null, DateTimeOffset? ExpiresAtUtc = null, bool IsTrial = false);

    /// <summary>
    /// Crée la licence d'une session Checkout payée, si ce n'est déjà fait. Appelé par le webhook
    /// <c>checkout.session.completed</c> <b>et</b> par la page de confirmation : le premier arrivé crée
    /// la licence, le second la retrouve.
    /// </summary>
    public async Task<SubscriptionRecord?> ProvisionAsync(string sessionId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (await _subscriptions.FindBySessionAsync(sessionId, ct) is { } existing) return existing;

            var session = await new CheckoutSessionService(_stripe!).GetAsync(sessionId,
                new Stripe.Checkout.SessionGetOptions { Expand = ["subscription"] }, cancellationToken: ct);
            if (session.Status != "complete" || session.Subscription is null) return null;

            var subscription = session.Subscription;
            var isTrial = session.Metadata.GetValueOrDefault("plan") == Plans.Trial;
            var plan = ResolvePlan(subscription, fallback: session.Metadata.GetValueOrDefault("plan"));
            if (plan is null)
            {
                _logger.LogError("Session {Session} : offre introuvable (prix {Price}).", sessionId, PriceOf(subscription));
                return null;
            }

            var email = session.CustomerDetails?.Email ?? session.CustomerEmail;
            var clientName = FirstNonEmpty(session.CustomerDetails?.BusinessName, session.CustomerDetails?.Name, email)
                             ?? "Client Stripe";

            var key = LicenseKeyGenerator.Generate();
            var license = await _licenses.CreateAsync(clientName[..Math.Min(clientName.Length, 120)], key,
                ComputeExpiry(subscription), plan.AllowedTools, plan.MaxMachines, ct);

            var now = DateTimeOffset.UtcNow;
            var record = new SubscriptionRecord(sessionId, session.CustomerId, subscription.Id, license.Id, plan.Code,
                subscription.Status, email, isTrial, _protector.Protect(key), now, now);
            await _subscriptions.InsertAsync(record, ct);

            _logger.LogInformation("Licence {License} créée pour {Client} (offre {Plan}, abonnement {Subscription}).",
                license.Id, clientName, plan.Code, subscription.Id);
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Page de confirmation : provisionne au besoin, puis renvoie la clé en clair. La clé n'est
    /// réaffichée que pendant <c>Shop:KeyRevealDays</c> jours — l'identifiant de session reste dans
    /// l'historique du navigateur, il ne doit pas donner accès à la clé indéfiniment.
    /// </summary>
    public async Task<CheckoutResult> GetCheckoutResultAsync(string sessionId, CancellationToken ct)
    {
        var record = await ProvisionAsync(sessionId, ct);
        if (record is null) return new CheckoutResult("pending");

        var license = await _licenses.FindByIdAsync(record.LicenseId, ct);
        if (license is null) return new CheckoutResult("pending");

        var planName = record.IsTrial ? Plans.Find(Plans.Trial)!.Name : Plans.Find(record.Plan)?.Name;
        if (record.ProtectedLicenseKey is null || record.CreatedAtUtc.AddDays(KeyRevealDays) < DateTimeOffset.UtcNow)
        {
            return new CheckoutResult("expired", planName, license.ClientName, record.Email);
        }

        return new CheckoutResult("ready", planName, license.ClientName, record.Email,
            _protector.Unprotect(record.ProtectedLicenseKey), license.ExpiresAtUtc, record.IsTrial);
    }

    /// <summary>
    /// Recopie l'état courant d'un abonnement Stripe sur sa licence : offre (donc outils autorisés),
    /// échéance, nombre de postes. Appelé pour tout événement <c>customer.subscription.*</c>.
    /// </summary>
    public async Task SyncSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Abonnement pas encore provisionné (l'événement précède checkout.session.completed) : le
            // provisionnement lira lui-même l'état à jour, rien à faire ici.
            var record = await _subscriptions.FindBySubscriptionAsync(subscriptionId, ct);
            if (record is null) return;

            var subscription = await new SubscriptionService(_stripe!).GetAsync(subscriptionId, cancellationToken: ct);
            var plan = ResolvePlan(subscription, fallback: record.Plan);
            if (plan is null)
            {
                _logger.LogError("Abonnement {Subscription} : offre introuvable (prix {Price}).", subscriptionId,
                    PriceOf(subscription));
                return;
            }

            var updated = await _licenses.UpdateAsync(record.LicenseId, clientName: null,
                expiresAtUtc: ComputeExpiry(subscription), isRevoked: null, allowedTools: plan.AllowedTools,
                clearAllowedTools: plan.AllowedTools is null, maxMachines: plan.MaxMachines, ct);
            if (!updated)
            {
                _logger.LogWarning("Abonnement {Subscription} : la licence {License} a été supprimée.",
                    subscriptionId, record.LicenseId);
                return;
            }

            await _subscriptions.UpdateStateAsync(subscriptionId, plan.Code, subscription.Status, ct);
            _logger.LogInformation("Abonnement {Subscription} ({Status}, offre {Plan}) appliqué à la licence {License}.",
                subscriptionId, subscription.Status, plan.Code, record.LicenseId);
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- Espace client -------------------------------------------------------------------------

    /// <summary>
    /// Ouvre le portail client Stripe (factures, moyen de paiement, changement d'offre, résiliation).
    /// Le client s'identifie par sa clé de licence : c'est le seul secret qu'il détient chez nous.
    /// </summary>
    public async Task<string?> CreatePortalUrlAsync(string licenseKey, string baseUrl, CancellationToken ct)
    {
        var license = await _licenses.FindByKeyAsync(licenseKey, ct);
        if (license is null) return null;

        var record = await _subscriptions.FindByLicenseAsync(license.Id, ct);
        if (record is null) return null;

        var portal = await new PortalSessionService(_stripe!).CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = record.StripeCustomerId,
            ReturnUrl = $"{baseUrl}/",
            Locale = "fr",
        }, cancellationToken: ct);
        return portal.Url;
    }

    // --- Règles ----------------------------------------------------------------------------------

    /// <summary>
    /// Pendant l'essai : toutes les options. Ensuite : l'offre du prix facturé — c'est ce qui suit un
    /// changement d'offre fait depuis le portail. <paramref name="fallback"/> (métadonnée ou dernière
    /// offre connue) ne sert que si le prix n'est relié à aucune offre dans la configuration.
    /// </summary>
    private Plan? ResolvePlan(Subscription subscription, string? fallback)
    {
        if (subscription.Status == "trialing") return Plans.Find(Plans.Trial);
        return PlanForPrice(PriceOf(subscription)) ?? Plans.Find(fallback is Plans.Trial ? Plans.Production : fallback);
    }

    /// <summary>
    /// Échéance de la licence. Abonnement payé : fin de la période en cours, plus quelques jours de
    /// grâce pour laisser à Stripe le temps de relancer un paiement refusé. Essai : sa date de fin, sans
    /// grâce. Abonnement terminé, impayé ou incomplet : immédiatement.
    /// </summary>
    private DateTimeOffset ComputeExpiry(Subscription subscription)
    {
        var periodEnd = subscription.Items?.Data is { Count: > 0 } items
            ? Utc(items.Max(i => i.CurrentPeriodEnd))
            : DateTimeOffset.UtcNow;

        return subscription.Status switch
        {
            "trialing" => subscription.TrialEnd is { } trialEnd ? Utc(trialEnd) : periodEnd,
            "active" or "past_due" => periodEnd.AddDays(GraceDays),
            _ => subscription.EndedAt is { } endedAt ? Utc(endedAt) : DateTimeOffset.UtcNow,
        };
    }

    private static string? PriceOf(Subscription subscription) => subscription.Items?.Data.FirstOrDefault()?.Price?.Id;

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
