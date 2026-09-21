<#
.SYNOPSIS
    Publie le serveur MCP en paquet zip prêt à être diffusé par le shim de lancement,
    et l'enregistre au besoin auprès du serveur de licences.

.DESCRIPTION
    Le numéro de version est lu dans <Version> de Sage100Mcp.csproj : c'est lui qui pilote tout le
    mécanisme de mise à jour, il doit donc être incrémenté avant de publier.

    Le paquet contient les binaires à sa racine (Sage100Mcp.exe et ses dépendances) et un
    appsettings.json *modèle*. Le appsettings.json de développement n'est jamais empaqueté : il
    contient une vraie clé de licence et les chaînes de connexion locales.

.EXAMPLE
    # Fabriquer le paquet et afficher son empreinte
    .\publish-release.ps1

.EXAMPLE
    # Fabriquer et enregistrer la version auprès du serveur de licences
    .\publish-release.ps1 -LicenseServerUrl https://mon-serveur -AdminKey '…' `
                          -DownloadUrl https://dl.exemple.fr/sage100mcp-1.2.0.zip
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [string]$OutputDir,

    # Enregistrement auprès du serveur de licences (les trois sont requis ensemble)
    [string]$LicenseServerUrl,
    [string]$AdminKey,
    [string]$DownloadUrl,

    [string]$Channel = 'stable',
    [switch]$SetMinimum,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'

$srcDir = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $srcDir 'Sage100Mcp\Sage100Mcp.csproj'
if (-not (Test-Path $projectPath)) { throw "Projet introuvable : $projectPath" }

if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path -Parent $srcDir) 'artifacts' }

# --- Version diffusée ------------------------------------------------------------------------
$version = ([xml](Get-Content $projectPath)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Aucun <Version> dans $projectPath : impossible de publier sans numéro de version." }
if (-not [Version]::TryParse($version, [ref]([Version]::new()))) {
    throw "Version '$version' invalide. Format attendu Major.Minor.Patch (les préversions ne sont pas gérées)."
}
Write-Host "Version à publier : $version" -ForegroundColor Cyan

# --- Publication -----------------------------------------------------------------------------
$stageDir = Join-Path $OutputDir "stage-$version"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$publishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $stageDir,
    "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())"
)
if (-not $SelfContained) { $publishArgs += '-p:PublishSingleFile=false' }

& dotnet @publishArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué (code $LASTEXITCODE)." }

$serverExe = Join-Path $stageDir 'Sage100Mcp.exe'
if (-not (Test-Path $serverExe)) { throw "Sage100Mcp.exe absent de la publication : $stageDir" }

# --- appsettings.json : modèle, jamais celui de développement --------------------------------
# Le shim ne lit de toute façon que celui de la racine d'installation ; ce modèle sert uniquement
# à amorcer une installation neuve.
$template = [ordered]@{
    License = [ordered]@{ ServerUrl = ''; LicenseKey = ''; TimeoutSeconds = 5; GracePeriodDays = 3 }
    Update  = [ordered]@{ Enabled = $true; CheckIntervalHours = 4; RequirePublisher = '' }
    Mcp     = [ordered]@{ Http = [ordered]@{ Urls = 'http://127.0.0.1:5099'; EndpointPath = '/mcp'; ApiKeys = @(); AllowedOrigins = @() } }
    Sage    = [ordered]@{ Databases = @([ordered]@{
        Name = 'À_RENSEIGNER'
        Description = 'Base Sage 100 du client'
        ConnectionString = 'Server=localhost;Database=À_RENSEIGNER;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;Application Name=Sage100-MCP'
        Default = $true
    }) }
}
$template | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $stageDir 'appsettings.json') -Encoding utf8

# --- Paquet ----------------------------------------------------------------------------------
$zipPath = Join-Path $OutputDir "sage100mcp-$version-$Runtime.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath
Remove-Item $stageDir -Recurse -Force

$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$sizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "Paquet   : $zipPath ($sizeMb Mo)" -ForegroundColor Green
Write-Host "Version  : $version"
Write-Host "SHA-256  : $sha256"

if (-not $SelfContained) {
    Write-Host "Note     : paquet dépendant du framework — le runtime .NET $Runtime doit être installé chez le client." -ForegroundColor Yellow
}
Write-Host "Rappel   : signez Sage100Mcp.exe avant diffusion, sinon le shim ne peut pas authentifier la version." -ForegroundColor Yellow

# --- Enregistrement auprès du serveur de licences --------------------------------------------
if ($LicenseServerUrl -and $AdminKey -and $DownloadUrl) {
    $body = [ordered]@{
        version     = $version
        downloadUrl = $DownloadUrl
        sha256      = $sha256
        channel     = $Channel
        isMinimum   = [bool]$SetMinimum
    }
    if ($Notes) { $body.notes = $Notes }

    $release = Invoke-RestMethod -Uri "$($LicenseServerUrl.TrimEnd('/'))/api/admin/releases" -Method Post `
        -Headers @{ 'X-Admin-Key' = $AdminKey; 'Content-Type' = 'application/json' } `
        -Body ($body | ConvertTo-Json)

    Write-Host ""
    Write-Host "Version $($release.version) enregistrée sur le canal $($release.channel)." -ForegroundColor Green
    if ($SetMinimum) { Write-Host "Plancher de version : mise à jour obligatoire pour tous les clients en dessous." -ForegroundColor Yellow }
}
elseif ($LicenseServerUrl -or $AdminKey -or $DownloadUrl) {
    Write-Host ""
    Write-Host "Enregistrement ignoré : -LicenseServerUrl, -AdminKey et -DownloadUrl sont requis ensemble." -ForegroundColor Yellow
}
