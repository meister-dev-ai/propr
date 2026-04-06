#Requires -Version 7.0
<#
.SYNOPSIS
    Deploys the full meister-propr stack to Azure Container Apps.

.DESCRIPTION
    1. Collects and validates all parameters
    2. Deploys infrastructure and app resources via Bicep using public images

.PARAMETER Config
    Hashtable supplying any or all parameters. Individual parameters take
    precedence over Config values. Secrets may be plain strings or SecureStrings.

.EXAMPLE
    # Interactive — prompts for anything not supplied
    ./example/azure/.azure/deploy.ps1 -ResourceGroup my-rg

.EXAMPLE
    # Fully scripted via config object
    $cfg = @{
        ResourceGroup      = 'my-rg'
        Location           = 'westeurope'
        BootstrapAdminUser = 'admin'
        BootstrapAdminPassword = '...'
        JwtSecret          = 'a-32-character-or-longer-random-secret'
        DbUser             = 'postgres'
        DbPassword         = '...'
    }
    ./example/azure/.azure/deploy.ps1 -Config $cfg
#>
[CmdletBinding()]
param(
    [hashtable]$Config,

    [string]$ResourceGroup,
    [string]$ProjectName,
    [string]$Location,
    [string]$ImageTag,
    [string]$BootstrapAdminUser,
    [object]$BootstrapAdminPassword, # string or SecureString
    [object]$JwtSecret,              # string or SecureString
    [object]$DbConnectionString,    # string or SecureString
    [object]$DbUser,                # string or SecureString
    [object]$DbPassword             # string or SecureString
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Message) {
    Write-Host "`n── $Message" -ForegroundColor Cyan
}

function Write-Success([string]$Message) {
    Write-Host "   ✓ $Message" -ForegroundColor Green
}

function Write-Fail([string]$Message) {
    Write-Host "   ✗ $Message" -ForegroundColor Red
}

function ConvertTo-PlainText([object]$Value) {
    if ($Value -is [SecureString]) {
        return ConvertFrom-SecureString -SecureString $Value -AsPlainText
    }
    return [string]$Value
}

function Assert-NotEmpty([object]$Value, [string]$Name) {
    $plain = ConvertTo-PlainText $Value
    if ([string]::IsNullOrWhiteSpace($plain)) {
        Write-Fail "$Name is required."
        exit 1
    }
}

function Assert-MinLength([object]$Value, [string]$Name, [int]$MinLength) {
    $plain = ConvertTo-PlainText $Value
    if ($plain.Length -lt $MinLength) {
        Write-Fail "$Name must be at least $MinLength characters long."
        exit 1
    }
}

$script:PublishedBackendTags = $null
$script:PublishedAdminUiTags = $null

function Get-GhcrRepositoryTags([string]$Repository) {
    $uri = "https://ghcr.io/v2/$Repository/tags/list"
    $challenge = Invoke-WebRequest -Uri $uri -SkipHttpErrorCheck -UseBasicParsing

    if ($challenge.StatusCode -eq 200) {
        return @((ConvertFrom-Json $challenge.Content).tags)
    }

    if ($challenge.StatusCode -ne 401) {
        throw "GHCR query for '$Repository' returned HTTP $($challenge.StatusCode)."
    }

    $authHeader = @($challenge.Headers['WWW-Authenticate'])[0]
    if ([string]::IsNullOrWhiteSpace($authHeader)) {
        throw "GHCR query for '$Repository' did not return a WWW-Authenticate header."
    }

    $realm = [regex]::Match($authHeader, 'realm="([^"]+)"').Groups[1].Value
    $service = [regex]::Match($authHeader, 'service="([^"]+)"').Groups[1].Value
    $scope = [regex]::Match($authHeader, 'scope="([^"]+)"').Groups[1].Value

    if ([string]::IsNullOrWhiteSpace($realm) -or [string]::IsNullOrWhiteSpace($service) -or [string]::IsNullOrWhiteSpace($scope)) {
        throw "Unable to parse GHCR auth challenge for '$Repository'."
    }

    $tokenUri = "${realm}?service=$service&scope=$scope"
    $token = (Invoke-RestMethod -Uri $tokenUri -UseBasicParsing).token
    $response = Invoke-RestMethod -Uri $uri -Headers @{ Authorization = "Bearer $token" } -UseBasicParsing
    return @($response.tags)
}

function Get-PublishedImageTagSets() {
    if (-not $script:PublishedBackendTags) {
        $script:PublishedBackendTags = Get-GhcrRepositoryTags 'meister-dev-ai/propr'
    }

    if (-not $script:PublishedAdminUiTags) {
        $script:PublishedAdminUiTags = Get-GhcrRepositoryTags 'meister-dev-ai/propr/admin-ui'
    }

    return [pscustomobject]@{
        Backend = $script:PublishedBackendTags
        AdminUi = $script:PublishedAdminUiTags
    }
}

function Resolve-PublishedImageTag() {
    $tagSets = Get-PublishedImageTagSets

    if (($tagSets.Backend -contains 'latest') -and ($tagSets.AdminUi -contains 'latest')) {
        return 'latest'
    }

    $commonTags = $tagSets.Backend |
        Where-Object { $_ -ne 'latest' -and $_ -notlike 'sha256-*' -and ($tagSets.AdminUi -contains $_) } |
        Sort-Object

    if (-not $commonTags) {
        throw 'No common published GHCR tag was found for backend and admin-ui images.'
    }

    return $commonTags[-1]
}

function Assert-PublishedImageTag([string]$Tag) {
    $tagSets = Get-PublishedImageTagSets
    if ($tagSets.Backend -notcontains $Tag) {
        throw "Backend image tag '$Tag' is not published in GHCR."
    }

    if ($tagSets.AdminUi -notcontains $Tag) {
        throw "Admin UI image tag '$Tag' is not published in GHCR."
    }
}

function Use-Config([string]$Key, [ref]$Target, [bool]$IsSecret = $false) {
    if ($Config -and $Config.ContainsKey($Key) -and -not $Target.Value) {
        $val = $Config[$Key]
        if ($IsSecret -and $val -is [string]) {
            $Target.Value = ConvertTo-SecureString $val -AsPlainText -Force
        } else {
            $Target.Value = $val
        }
    }
}

Write-Host ""
Write-Host "  meister-propr Azure Deployment" -ForegroundColor White
Write-Host "  ────────────────────────────────" -ForegroundColor DarkGray
Write-Host ""

Use-Config 'ResourceGroup'      ([ref]$ResourceGroup)
Use-Config 'ProjectName'        ([ref]$ProjectName)
Use-Config 'Location'           ([ref]$Location)
if (-not $ProjectName)      { $ProjectName      = 'meister-propr' }
if (-not $Location)         { $Location         = 'switzerlandnorth' }
Use-Config 'ImageTag'           ([ref]$ImageTag)
Use-Config 'BootstrapAdminUser' ([ref]$BootstrapAdminUser)
Use-Config 'BootstrapAdminPassword' ([ref]$BootstrapAdminPassword) -IsSecret $true
Use-Config 'JwtSecret'          ([ref]$JwtSecret)          -IsSecret $true
Use-Config 'DbConnectionString' ([ref]$DbConnectionString) -IsSecret $true
Use-Config 'DbUser'             ([ref]$DbUser)             -IsSecret $true
Use-Config 'DbPassword'         ([ref]$DbPassword)         -IsSecret $true

if (-not $BootstrapAdminUser) { $BootstrapAdminUser = 'admin' }

Write-Step "Checking prerequisites"

if (-not (Get-Command 'az' -ErrorAction SilentlyContinue)) {
    Write-Fail "'az' (Azure CLI) is not installed or not on PATH."
    exit 1
}
Write-Success "az found"

Write-Step "Resolving published image tag"

try {
    if (-not $ImageTag) {
        $ImageTag = Resolve-PublishedImageTag
        Write-Success "Using published tag '$ImageTag'"
    }
    else {
        Assert-PublishedImageTag $ImageTag
        Write-Success "Verified published tag '$ImageTag'"
    }
}
catch {
    Write-Fail $_.Exception.Message
    exit 1
}

Write-Step "Collecting parameters"

if (-not $ResourceGroup)      { $ResourceGroup      = Read-Host "Resource group name" }
if (-not $BootstrapAdminPassword) { $BootstrapAdminPassword = Read-Host "Bootstrap admin password" -AsSecureString }
if (-not $JwtSecret)          { $JwtSecret          = Read-Host "JWT signing secret (32+ chars)" -AsSecureString }
if (-not $DbUser)             { $DbUser             = Read-Host "PostgreSQL username"              -AsSecureString }
if (-not $DbPassword)         { $DbPassword         = Read-Host "PostgreSQL password"              -AsSecureString }

Write-Step "Validating parameters"

Assert-NotEmpty $ResourceGroup      'ResourceGroup'
Assert-NotEmpty $ProjectName        'ProjectName'
Assert-NotEmpty $Location           'Location'
Assert-NotEmpty $ImageTag           'ImageTag'
Assert-NotEmpty $BootstrapAdminUser 'BootstrapAdminUser'
Assert-NotEmpty $BootstrapAdminPassword 'BootstrapAdminPassword'
Assert-NotEmpty $JwtSecret          'JwtSecret'
Assert-MinLength $JwtSecret         'JwtSecret' 32
Assert-NotEmpty $DbUser             'DbUser'
Assert-NotEmpty $DbPassword         'DbPassword'

$rgExists = az group exists --name $ResourceGroup | ConvertFrom-Json
if (-not $rgExists) {
    Write-Fail "Resource group '$ResourceGroup' does not exist."
    exit 1
}

Write-Success "All parameters valid"

$BackendImage      = "ghcr.io/meister-dev-ai/propr`:$ImageTag"
$AdminUiImage      = "ghcr.io/meister-dev-ai/propr/admin-ui`:$ImageTag"
$ReverseProxyImage = "nginx:alpine"
$DbImage           = "pgvector/pgvector:pg17"

Write-Host ""
Write-Host "  Deployment summary" -ForegroundColor White
Write-Host "  ──────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host "  Resource group  : $ResourceGroup"
Write-Host "  Project name    : $ProjectName"
Write-Host "  Location        : $Location"
Write-Host "  Image tag       : $ImageTag"
Write-Host "  Bootstrap admin : $BootstrapAdminUser"
Write-Host "  Backend image   : $BackendImage"
Write-Host "  Admin UI image  : $AdminUiImage"
Write-Host "  Reverse proxy   : $ReverseProxyImage"
Write-Host "  Database image  : $DbImage"
Write-Host "  DB connection   : $(if ($DbConnectionString) { 'override provided' } else { 'derived from internal db app' })"
Write-Host ""

$confirm = Read-Host "Proceed? (y/N)"
if ($confirm -notmatch '^[Yy]$') {
    Write-Host "Aborted." -ForegroundColor Yellow
    exit 0
}

function Get-BicepParams([bool]$DeployApps) {
    return @(
        "projectName=$ProjectName"
        "location=$Location"
        "imageTag=$ImageTag"
        "deployApps=$($DeployApps.ToString().ToLower())"
        "bootstrapAdminUser=$BootstrapAdminUser"
        "bootstrapAdminPassword=$(ConvertTo-PlainText $BootstrapAdminPassword)"
        "jwtSecret=$(ConvertTo-PlainText $JwtSecret)"
        "dbConnectionString=$(ConvertTo-PlainText $DbConnectionString)"
        "dbUser=$(ConvertTo-PlainText $DbUser)"
        "dbPassword=$(ConvertTo-PlainText $DbPassword)"
    )
}

Write-Step "Deploying infrastructure and container apps"

az deployment group create `
    --resource-group $ResourceGroup `
    --template-file "$PSScriptRoot/main.bicep" `
    --parameters (Get-BicepParams $true) `
    --output none

if ($LASTEXITCODE -ne 0) { Write-Fail "Container apps deployment failed."; exit 1 }
Write-Success "Container apps deployed"

$url = az deployment group show `
    --resource-group $ResourceGroup `
    --name main `
    --query properties.outputs.reverseProxyUrl.value -o tsv

Write-Host ""
Write-Host "  Deployment complete!" -ForegroundColor Green
Write-Host "  URL: $url" -ForegroundColor White
Write-Host ""
