#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Starts the MeisterProPR dev environment (backend, admin-ui, nginx) without Docker.

.DESCRIPTION
    Builds the admin-ui, then runs nginx as a Podman container that proxies requests
    to the host. Optionally starts the .NET backend. Use -SkipBackend to launch the
    backend from your IDE (VS Code, Rider, Visual Studio) with a debugger attached.

    Prerequisites:
      - podman in PATH  (https://podman.io)
      - Node.js / npm in PATH
      - .NET 10 SDK in PATH  (only needed without -SkipBackend)

    nginx container listens on http://localhost:5080 and routes:
      /admin/*  →  admin-ui/dist/  (static SPA files, volume-mounted)
      /*        →  http://host.containers.internal:8080  (ASP.NET Core backend)

    NOTE: env vars from .env are passed only to the backend child process.
    They are NOT set in the current terminal session, so running 'podman compose up'
    afterwards in the same terminal is safe.

.PARAMETER SkipBackend
    Do not start the backend. Launch it from your IDE for debugging.
    The backend must listen on http://localhost:<BackendPort> (default 8080).

.PARAMETER SkipBuild
    Skip 'npm run build'. Use the existing admin-ui/dist/ directory.

.PARAMETER BackendPort
    Port the backend listens on on the host. Default: 8080.

.PARAMETER ProxyPort
    Host port the nginx container exposes. Default: 5080.

.PARAMETER DbConnectionString
    PostgreSQL connection string passed to the backend.
    Defaults to the local postgres instance (same credentials as docker-compose).

.EXAMPLE
    .\scripts\Start-DevEnvironment.ps1 -SkipBackend

.EXAMPLE
    .\scripts\Start-DevEnvironment.ps1 -SkipBackend -SkipBuild

.EXAMPLE
    .\scripts\Start-DevEnvironment.ps1 -DbConnectionString "Host=localhost;Port=5432;Database=meisterpropr;Username=myuser;Password=mypass"
#>
param(
    [switch]$SkipBackend,
    [switch]$SkipBuild,
    [int]$BackendPort       = 8080,
    [int]$ProxyPort         = 5080,
    [string]$DbConnectionString = 'Host=localhost;Port=5432;Database=meisterpropr;Username=postgres;Password=devpass'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ContainerName = 'meisterpropr-nginx-dev'

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$RepoRoot    = Split-Path $PSScriptRoot -Parent
$ApiProject  = Join-Path $RepoRoot 'src\MeisterProPR.Api\MeisterProPR.Api.csproj'
$AdminUiDir  = Join-Path $RepoRoot 'admin-ui'
$AdminUiDist = Join-Path $AdminUiDir 'dist'
$TempDir     = Join-Path $RepoRoot '.dev-env'
$NginxConf   = Join-Path $TempDir 'nginx.conf'
$EnvFile     = Join-Path $RepoRoot '.env'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"   -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "    WARNING: $msg" -ForegroundColor Yellow }

function Require-Command([string]$name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        Write-Host "ERROR: '$name' not found in PATH." -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Preflight checks
# ---------------------------------------------------------------------------
Write-Step 'Checking prerequisites'

Require-Command 'podman'
Require-Command 'npm'
if (-not $SkipBackend) { Require-Command 'dotnet' }

Write-Ok "podman : $((Get-Command podman).Source)"
Write-Ok "npm    : $((Get-Command npm).Source)"
if (-not $SkipBackend) { Write-Ok "dotnet : $((Get-Command dotnet).Source)" }

# ---------------------------------------------------------------------------
# Temp directory for generated nginx config
# ---------------------------------------------------------------------------
if (-not (Test-Path $TempDir)) { New-Item -ItemType Directory -Path $TempDir | Out-Null }

# ---------------------------------------------------------------------------
# Build admin-ui
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Write-Step 'Skipping admin-ui build (-SkipBuild)'
    if (-not (Test-Path $AdminUiDist)) {
        Write-Host "ERROR: $AdminUiDist not found. Run without -SkipBuild first." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Step 'Building admin-ui'
    Push-Location $AdminUiDir
    try {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'npm run build failed' }
    } finally {
        Pop-Location
    }
    Write-Ok 'admin-ui built'
}

# ---------------------------------------------------------------------------
# Generate nginx server config
# ---------------------------------------------------------------------------
Write-Step 'Generating nginx config'

@"
server {
    listen 80;
    server_name localhost;

    location /admin/ {
        alias   /usr/share/nginx/html/admin/;
        index   index.html;
        try_files `$uri `$uri/ /admin/index.html;
    }

    location / {
        proxy_pass         http://host.containers.internal:$BackendPort;
        proxy_set_header   Host              `$host;
        proxy_set_header   X-Real-IP         `$remote_addr;
        proxy_set_header   X-Forwarded-For   `$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto `$scheme;
        proxy_read_timeout 300s;
    }
}
"@ | Set-Content -Path $NginxConf -Encoding UTF8

Write-Ok "Written: $NginxConf"

# ---------------------------------------------------------------------------
# Remove any stale nginx container
# ---------------------------------------------------------------------------
$existing = & podman ps -a --filter "name=^$ContainerName$" --format '{{.Names}}' 2>$null
if ($existing -match $ContainerName) {
    Write-Warn "Removing stale container '$ContainerName'"
    & podman rm -f $ContainerName | Out-Null
}

# ---------------------------------------------------------------------------
# Start nginx container
# ---------------------------------------------------------------------------
Write-Step "Starting nginx container ($ContainerName)"

& podman run -d --rm `
    --name $ContainerName `
    -p "${ProxyPort}:80" `
    -v "${NginxConf}:/etc/nginx/conf.d/default.conf:ro" `
    -v "${AdminUiDist}:/usr/share/nginx/html/admin:ro" `
    nginx:alpine | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: podman run failed.' -ForegroundColor Red
    exit 1
}

Write-Ok "nginx container running  →  http://localhost:$ProxyPort"

# ---------------------------------------------------------------------------
# Load .env and build backend env vars
# ---------------------------------------------------------------------------
Write-Step 'Loading environment'

# Start with current process environment as base, then override/add our vars.
# We use ProcessStartInfo so these vars go ONLY to the backend child process
# and do NOT pollute the current terminal session.
$backendEnv = [System.Collections.Generic.Dictionary[string,string]]::new()

# Inherit current process environment
foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
    $backendEnv[$entry.Key] = $entry.Value
}

# Layer in .env file
if (Test-Path $EnvFile) {
    $loaded = 0
    foreach ($line in Get-Content $EnvFile) {
        $line = $line.Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $idx = $line.IndexOf('=')
        if ($idx -lt 1) { continue }
        $key   = $line.Substring(0, $idx).Trim()
        $value = $line.Substring($idx + 1).Trim()
        $backendEnv[$key] = $value
        $loaded++
    }
    Write-Ok "Loaded $loaded vars from .env"
} else {
    Write-Warn '.env not found'
}

# Always set/override these for local dev
$backendEnv['ASPNETCORE_ENVIRONMENT'] = 'Development'
$backendEnv['ASPNETCORE_URLS']        = "http://localhost:$BackendPort"
$backendEnv['DB_CONNECTION_STRING']   = $DbConnectionString

# ---------------------------------------------------------------------------
# Start backend
# ---------------------------------------------------------------------------
$backendProc = $null

if ($SkipBackend) {
    Write-Host ''
    Write-Host '  Backend not started (-SkipBackend is set).' -ForegroundColor Yellow
    Write-Host "  Start it from your IDE listening on http://localhost:$BackendPort" -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Required env vars for your run/debug config:' -ForegroundColor DarkGray
    Write-Host "    ASPNETCORE_URLS=http://localhost:$BackendPort" -ForegroundColor White
    Write-Host '    ASPNETCORE_ENVIRONMENT=Development' -ForegroundColor White
    Write-Host "    DB_CONNECTION_STRING=$DbConnectionString" -ForegroundColor White
} else {
    Write-Step 'Starting backend (runs in this terminal — attach debugger to dotnet.exe if needed)'

    # Use ProcessStartInfo to pass env vars ONLY to the child process.
    # UseShellExecute = false is required to use psi.Environment; the process inherits
    # the current console window (no new window is opened). This avoids polluting the
    # current terminal session environment, which would break 'podman compose up' if
    # run afterwards in the same terminal.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName         = 'dotnet'
    $psi.Arguments        = "run --project `"$ApiProject`" --no-launch-profile"
    $psi.UseShellExecute  = $false   # required to use psi.Environment
    $psi.CreateNoWindow   = $false

    $psi.Environment.Clear()
    foreach ($kv in $backendEnv.GetEnumerator()) {
        $psi.Environment[$kv.Key] = $kv.Value
    }

    $backendProc = [System.Diagnostics.Process]::Start($psi)
    Write-Ok "Backend starting (PID $($backendProc.Id))"
    Write-Ok "Listening on http://localhost:$BackendPort"
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '  ┌──────────────────────────────────────────────────┐' -ForegroundColor Cyan
Write-Host '  │  MeisterProPR dev environment running            │' -ForegroundColor Cyan
Write-Host '  ├──────────────────────────────────────────────────┤' -ForegroundColor Cyan
Write-Host "  │  Admin UI  →  http://localhost:$ProxyPort/admin/        │" -ForegroundColor Cyan
Write-Host "  │  API       →  http://localhost:$ProxyPort               │" -ForegroundColor Cyan
Write-Host "  │  Backend   →  http://localhost:$BackendPort (direct)    │" -ForegroundColor Cyan
Write-Host '  │                                                  │' -ForegroundColor Cyan
Write-Host '  │  Ctrl+C to stop all services                    │' -ForegroundColor Cyan
Write-Host '  └──────────────────────────────────────────────────┘' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------------------
# Cleanup
# ---------------------------------------------------------------------------
function Stop-All {
    Write-Host "`n==> Shutting down..." -ForegroundColor Yellow

    if ($null -ne $backendProc -and -not $backendProc.HasExited) {
        Write-Host "    Stopping backend (PID $($backendProc.Id))..." -ForegroundColor Yellow
        $backendProc.Kill($true)
    }

    $running = & podman ps --filter "name=^$ContainerName$" --format '{{.Names}}' 2>$null
    if ($running -match $ContainerName) {
        Write-Host "    Stopping nginx container..." -ForegroundColor Yellow
        & podman stop $ContainerName | Out-Null
    }

    Write-Host '    Done.' -ForegroundColor Green
}

try {
    while ($true) {
        $running = & podman ps --filter "name=^$ContainerName$" --format '{{.Names}}' 2>$null
        if ($running -notmatch $ContainerName) {
            Write-Warn "nginx container stopped unexpectedly."
            break
        }

        if ($null -ne $backendProc -and $backendProc.HasExited) {
            Write-Warn "Backend exited unexpectedly (code $($backendProc.ExitCode))."
            $backendProc = $null
        }

        Start-Sleep -Seconds 3
    }
} finally {
    Stop-All
}
