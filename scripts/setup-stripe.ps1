<#
.SYNOPSIS
    Configure le compte Stripe de la boutique : offres, prix mensuels, webhook et portail client.

.DESCRIPTION
    Idempotent : relancé, il retrouve les prix par leur lookup_key et le webhook par son URL au lieu de
    les recréer. Il affiche à la fin les réglages à passer à deploy-license-server-azure.ps1.

    À lancer d'abord avec une clé de TEST (sk_test_…), vérifier un achat de bout en bout avec la carte
    4242 4242 4242 4242, puis relancer avec la clé LIVE (sk_live_…) : les deux modes ont des objets séparés.

    Les prix sont créés HORS TAXES (tax_behavior = exclusive), comme affiché sur le site. Pour que Stripe
    ajoute la TVA à la facture, activez Stripe Tax ou définissez un taux de TVA dans le tableau de bord.

.EXAMPLE
    .\setup-stripe.ps1 -SecretKey sk_test_… -SiteUrl https://sage-mcp-licences.azurewebsites.net
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SecretKey,

    # URL publique du site (sans / final) : le webhook est déclaré sur <SiteUrl>/api/stripe/webhook.
    [Parameter(Mandatory = $true)][string]$SiteUrl,

    [string]$ProductPrefix = 'MCP pour Sage 100'
)

$ErrorActionPreference = 'Stop'
$SiteUrl = $SiteUrl.TrimEnd('/')
$api = 'https://api.stripe.com/v1'
$headers = @{ Authorization = "Bearer $SecretKey" }

function Invoke-Stripe {
    param([string]$Method = 'Get', [string]$Path, [hashtable]$Body)
    try {
        if ($Method -eq 'Get') {
            return Invoke-RestMethod "$api/$Path" -Headers $headers -Method Get -Body $Body
        }
        return Invoke-RestMethod "$api/$Path" -Headers $headers -Method $Method -Body $Body `
            -ContentType 'application/x-www-form-urlencoded; charset=utf-8'
    }
    catch {
        $detail = $_.ErrorDetails.Message
        throw "Stripe $Method /$Path a échoué : $(if ($detail) { $detail } else { $_.Exception.Message })"
    }
}

$mode = if ($SecretKey.StartsWith('sk_live_')) { 'LIVE' } else { 'TEST' }
Write-Host "Compte Stripe en mode $mode" -ForegroundColor $(if ($mode -eq 'LIVE') { 'Yellow' } else { 'Cyan' })

# --- Offres et prix -------------------------------------------------------------------------------
# Le code d'offre (compta / gescom / production) doit correspondre à Shop/Plans.cs et à Stripe:Prices.
$offers = @(
    @{ Code = 'compta';     Name = 'Comptabilité';        Amount = 4900 },
    @{ Code = 'gescom';     Name = 'Gestion commerciale'; Amount = 9900 },
    @{ Code = 'production'; Name = 'Production';          Amount = 14900 }
)

$priceIds = @{}
$productIds = @{}
foreach ($offer in $offers) {
    $lookupKey = "sage-mcp-$($offer.Code)-mensuel"
    $existing = (Invoke-Stripe -Path 'prices' -Body @{ 'lookup_keys[]' = $lookupKey; active = 'true' }).data
    if ($existing) {
        $price = $existing[0]
        Write-Host "  $($offer.Name) : prix existant $($price.id)" -ForegroundColor DarkGray
    }
    else {
        $product = Invoke-Stripe -Method Post -Path 'products' -Body @{
            name                  = "$ProductPrefix — $($offer.Name)"
            'metadata[plan]'      = $offer.Code
        }
        $price = Invoke-Stripe -Method Post -Path 'prices' -Body @{
            product               = $product.id
            currency              = 'eur'
            unit_amount           = $offer.Amount
            'recurring[interval]' = 'month'
            tax_behavior          = 'exclusive'
            lookup_key            = $lookupKey
            'metadata[plan]'      = $offer.Code
        }
        Write-Host "  $($offer.Name) : prix créé $($price.id)" -ForegroundColor Green
    }
    $priceIds[$offer.Code] = $price.id
    $productIds[$offer.Code] = $price.product
}

# --- Webhook --------------------------------------------------------------------------------------
# Le secret de signature n'est lisible qu'à la création : un webhook existant est donc supprimé puis
# recréé, sinon on ne pourrait pas afficher le secret à reporter dans Stripe__WebhookSecret.
$webhookUrl = "$SiteUrl/api/stripe/webhook"
$endpoints = (Invoke-Stripe -Path 'webhook_endpoints' -Body @{ limit = 100 }).data
foreach ($old in @($endpoints | Where-Object { $_.url -eq $webhookUrl })) {
    Invoke-Stripe -Method Delete -Path "webhook_endpoints/$($old.id)" | Out-Null
    Write-Host "  Ancien webhook $($old.id) remplacé." -ForegroundColor DarkGray
}

$events = @(
    'checkout.session.completed',
    'customer.subscription.created',
    'customer.subscription.updated',
    'customer.subscription.deleted',
    'customer.subscription.paused',
    'customer.subscription.resumed'
)
$webhookBody = @{ url = $webhookUrl; description = 'Serveur de licences MCP pour Sage 100' }
for ($i = 0; $i -lt $events.Count; $i++) { $webhookBody["enabled_events[$i]"] = $events[$i] }
$webhook = Invoke-Stripe -Method Post -Path 'webhook_endpoints' -Body $webhookBody
Write-Host "  Webhook créé : $webhookUrl" -ForegroundColor Green

# --- Portail client -------------------------------------------------------------------------------
# Factures, moyen de paiement, résiliation en fin de période, et changement d'offre au prorata entre
# les trois prix — c'est ainsi qu'un client en essai passe à une offre payante sans changer de clé.
$portal = @{
    'business_profile[headline]'                                   = $ProductPrefix
    'business_profile[privacy_policy_url]'                         = "$SiteUrl/mentions-legales.html"
    'business_profile[terms_of_service_url]'                       = "$SiteUrl/cgv.html"
    'default_return_url'                                           = "$SiteUrl/"
    'features[invoice_history][enabled]'                           = 'true'
    'features[payment_method_update][enabled]'                     = 'true'
    'features[customer_update][enabled]'                           = 'true'
    'features[customer_update][allowed_updates][0]'                = 'address'
    'features[customer_update][allowed_updates][1]'                = 'name'
    'features[customer_update][allowed_updates][2]'                = 'tax_id'
    'features[customer_update][allowed_updates][3]'                = 'email'
    'features[subscription_cancel][enabled]'                       = 'true'
    'features[subscription_cancel][mode]'                          = 'at_period_end'
    'features[subscription_update][enabled]'                       = 'true'
    'features[subscription_update][proration_behavior]'            = 'create_prorations'
    'features[subscription_update][default_allowed_updates][0]'    = 'price'
}
$i = 0
foreach ($offer in $offers) {
    $portal["features[subscription_update][products][$i][product]"] = $productIds[$offer.Code]
    $portal["features[subscription_update][products][$i][prices][0]"] = $priceIds[$offer.Code]
    $i++
}

$configs = (Invoke-Stripe -Path 'billing_portal/configurations' -Body @{ is_default = 'true'; limit = 1 }).data
if ($configs) {
    Invoke-Stripe -Method Post -Path "billing_portal/configurations/$($configs[0].id)" -Body $portal | Out-Null
    Write-Host "  Portail client mis à jour." -ForegroundColor Green
}
else {
    Invoke-Stripe -Method Post -Path 'billing_portal/configurations' -Body $portal | Out-Null
    Write-Host "  Portail client créé." -ForegroundColor Green
}

# --- Récapitulatif --------------------------------------------------------------------------------
Write-Host ""
Write-Host "Réglages à appliquer au serveur de licences :" -ForegroundColor Cyan
Write-Host "  .\deploy-license-server-azure.ps1 -AppName <app> ``"
Write-Host "      -StripeSecretKey '<la clé secrète utilisée ici>' ``"
Write-Host "      -StripeWebhookSecret '$($webhook.secret)' ``"
Write-Host "      -StripePriceCompta '$($priceIds.compta)' -StripePriceGescom '$($priceIds.gescom)' -StripePriceProduction '$($priceIds.production)'"
Write-Host ""
Write-Host "Le secret du webhook n'est affiché qu'une fois : reportez-le maintenant." -ForegroundColor Yellow
