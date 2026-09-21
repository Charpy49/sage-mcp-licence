<#
.SYNOPSIS
    Déploie le serveur de licences sur Azure App Service (Linux), palier gratuit F1.

.DESCRIPTION
    Le script est idempotent : relancé, il met à jour l'application en place sans recréer les
    ressources ni régénérer la clé d'administration.

    Point essentiel : la base SQLite est placée dans /home/data/licenses.db et non sous
    /home/site/wwwroot. Les deux sont persistants sur App Service Linux, mais wwwroot est
    *remplacé* à chaque déploiement — une base qui y vivrait serait effacée à chaque publication,
    exactement le défaut que ce déploiement corrige.

    Prérequis : Azure CLI installé et session ouverte (az login).

.EXAMPLE
    .\deploy-license-server-azure.ps1 -AppName sage-mcp-licences

.EXAMPLE
    # Redéployer le code sans toucher aux réglages
    .\deploy-license-server-azure.ps1 -AppName sage-mcp-licences -CodeOnly
#>
[CmdletBinding()]
param(
    # Nom global unique : il donne l'URL https://<AppName>.azurewebsites.net
    [Parameter(Mandatory = $true)][string]$AppName,

    [string]$ResourceGroup = 'rg-sage-mcp-licences',
    [string]$Location = 'francecentral',

    # F1 = gratuit (1 Go de disque, quota de 60 min de CPU par jour, pas d'Always On).
    # B1 pour lever le quota et activer Always On (supprime le démarrage à froid).
    [string]$Sku = 'F1',

    # Laisser vide pour en générer une à la création ; ignoré si l'application en a déjà une.
    [string]$AdminKey,

    # Ne redéployer que le code, sans retoucher aux réglages d'application.
    [switch]$CodeOnly
)

$ErrorActionPreference = 'Stop'

$srcDir = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $srcDir 'Sage100Mcp.LicenseServer\Sage100Mcp.LicenseServer.csproj'
if (-not (Test-Path $projectPath)) { throw "Projet introuvable : $projectPath" }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI introuvable. Installe-le (winget install Microsoft.AzureCLI) puis lance 'az login'."
}

# --- Appels à az ------------------------------------------------------------------------------
# En PowerShell 5.1, chaque ligne de stderr d'un exécutable natif devient un ErrorRecord : sous
# ErrorActionPreference = 'Stop', les simples avertissements d'az ("Initiating deployment"…)
# interrompent le script, et l'interruption dépend même de la façon dont la sortie est consommée
# (un pipe suffit à la déclencher). On rétrograde donc localement et on juge sur le code de sortie,
# seul signal fiable.
function Invoke-Az {
    param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Arguments)

    $output = & { $ErrorActionPreference = 'Continue'; & az @Arguments 2>&1 }
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') a échoué (code $LASTEXITCODE) :`n$($output -join [Environment]::NewLine)"
    }
    # Les avertissements d'az sont écartés : seule la sortie standard porte du JSON exploitable.
    $output | Where-Object { $_ -is [string] -and $_ -notmatch '^(WARNING|Note):' }
}

$account = & { $ErrorActionPreference = 'Continue'; az account show --output json 2>$null }
if (-not $account) { throw "Aucune session Azure. Lance 'az login' d'abord." }
$subscription = ($account | ConvertFrom-Json).name
Write-Host "Abonnement : $subscription" -ForegroundColor Cyan

# --- Fabrication du zip de déploiement --------------------------------------------------------
# Compress-Archive de PowerShell 5.1 écrit les séparateurs de chemin en antislash ("runtimes\…"),
# ce qui n'est pas conforme au format zip et que Kudu, sous Linux, ne sait pas remettre en
# arborescence : le rsync vers /home/site/wwwroot échoue alors avec le code 23. On écrit donc les
# entrées à la main, en barres obliques.
function New-DeploymentZip {
    param([Parameter(Mandatory)][string]$SourceDir, [Parameter(Mandatory)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $root = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        foreach ($file in Get-ChildItem $SourceDir -Recurse -File) {
            $entryName = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, 'Optimal') | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

# --- Ressources ------------------------------------------------------------------------------
if (-not $CodeOnly) {
    Write-Host "Groupe de ressources $ResourceGroup ($Location)…" -ForegroundColor Cyan
    Invoke-Az group create --name $ResourceGroup --location $Location --output none

    # Un seul plan F1 par région et par abonnement : si la création échoue pour cette raison,
    # réutilise le plan existant ou change de région.
    $planName = "plan-$AppName"
    Write-Host "Plan App Service $planName (SKU $Sku, Linux)…" -ForegroundColor Cyan
    Invoke-Az appservice plan create --name $planName --resource-group $ResourceGroup `
        --location $Location --sku $Sku --is-linux --output none

    # 'az webapp list' filtré plutôt que 'az webapp show' : il renvoie une chaîne vide quand
    # l'application n'existe pas, là où 'show' échoue sur ResourceNotFound.
    $exists = Invoke-Az webapp list --resource-group $ResourceGroup --query "[?name=='$AppName'].name" --output tsv
    if (-not $exists) {
        # Le nom doit être libre à l'échelle mondiale (il porte le sous-domaine azurewebsites.net).
        Write-Host "Création de l'application $AppName…" -ForegroundColor Cyan
        Invoke-Az webapp create --name $AppName --resource-group $ResourceGroup --plan $planName `
            --runtime 'DOTNETCORE:10.0' --output none
    }
    else {
        Write-Host "Application $AppName déjà présente : réutilisée." -ForegroundColor DarkGray
    }

    # HTTPS obligatoire : la clé de licence et la clé d'administration transitent en clair sinon.
    Invoke-Az webapp update --name $AppName --resource-group $ResourceGroup --https-only true --output none

    # --- Réglages d'application ---------------------------------------------------------------
    # La clé existante n'est jamais écrasée : la régénérer invaliderait les scripts d'administration.
    $current = Invoke-Az webapp config appsettings list --name $AppName --resource-group $ResourceGroup --output json |
        ConvertFrom-Json
    $existingKey = ($current | Where-Object { $_.name -eq 'Licensing__AdminApiKey' }).value

    if ($existingKey) {
        Write-Host "Clé d'administration déjà configurée : conservée." -ForegroundColor DarkGray
        $effectiveKey = $null
    }
    elseif ($AdminKey) {
        $effectiveKey = $AdminKey
    }
    else {
        # RandomNumberGenerator::Fill et Convert::ToHexString n'existent pas sur .NET Framework,
        # sur lequel tourne PowerShell 5.1. Hexadécimal plutôt que base64 : pas de '+' ni de '/'
        # à échapper quand la clé voyage dans un en-tête HTTP ou une ligne de commande.
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $effectiveKey = -join ($bytes | ForEach-Object { $_.ToString('x2') })
        Write-Host ""
        Write-Host "Clé d'administration générée (notée une seule fois, conserve-la) :" -ForegroundColor Yellow
        Write-Host "  $effectiveKey" -ForegroundColor Yellow
        Write-Host ""
    }

    $settings = @('Licensing__DatabasePath=/home/data/licenses.db')
    if ($effectiveKey) { $settings += "Licensing__AdminApiKey=$effectiveKey" }

    Invoke-Az webapp config appsettings set --name $AppName --resource-group $ResourceGroup `
        --settings $settings --output none
}

# --- Publication du code ----------------------------------------------------------------------
$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) "ls-publish-$([Guid]::NewGuid().ToString('N'))"
$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "ls-$AppName.zip"

Write-Host "Publication du projet…" -ForegroundColor Cyan
# --artifacts-path déporte les sorties intermédiaires hors du projet. Deux raisons :
#  - une instance locale du serveur en cours d'exécution verrouille bin\Release et ferait échouer
#    la copie de l'exécutable ;
#  - avec -p:BaseOutputPath, MSBuild dépose l'arbre bin\ *dans* le dossier publié, qui partait
#    alors dans le zip et faisait échouer le rsync côté Kudu.
& dotnet publish $projectPath -c Release -o $stageDir --artifacts-path "$stageDir-art" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué (code $LASTEXITCODE)." }

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-DeploymentZip -SourceDir $stageDir -ZipPath $zipPath

Write-Host "Déploiement vers $AppName…" -ForegroundColor Cyan
Invoke-Az webapp deploy --name $AppName --resource-group $ResourceGroup --src-path $zipPath --type zip --output none

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$stageDir-art" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# --- Vérification -----------------------------------------------------------------------------
$base = "https://$AppName.azurewebsites.net"
Write-Host ""
Write-Host "URL : $base" -ForegroundColor Green

# Le premier appel réveille l'application : le délai est normal, il n'indique pas une panne.
try {
    $probe = Invoke-RestMethod "$base/api/license/validate" -Method Post -ContentType 'application/json' `
        -Body '{"licenseKey":""}' -TimeoutSec 90
    if ($probe.reason -eq 'missing_key') {
        Write-Host "Service opérationnel (réponse attendue 'missing_key')." -ForegroundColor Green
    }
    else {
        Write-Host "Réponse inattendue : $($probe | ConvertTo-Json -Compress)" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Vérification impossible : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Journaux : az webapp log tail --name $AppName --resource-group $ResourceGroup" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Étapes suivantes :" -ForegroundColor Cyan
Write-Host "  1. Réémettre les licences clients (celles de Heroku sont irrécupérables)."
Write-Host "  2. Publier les versions : .\publish-release.ps1 -LicenseServerUrl $base -AdminKey '…' -DownloadUrl '…'"
Write-Host "  3. Mettre License:ServerUrl = $base dans le appsettings.json des postes clients."
