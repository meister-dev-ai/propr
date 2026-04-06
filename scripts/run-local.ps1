Param(
    [string]$DbConnectionString = $env:DB_CONNECTION_STRING,
    [int]$BackendPort = 8080,
    [switch]$SkipUiInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ApiProject = Join-Path $RepoRoot 'src\MeisterProPR.Api\MeisterProPR.Api.csproj'
$ApiFolder  = Join-Path $RepoRoot 'src\MeisterProPR.Api'
$UiFolder   = Join-Path $RepoRoot 'admin-ui'
$EnvFile    = Join-Path $RepoRoot '.env'

if (-not $DbConnectionString) {
    Write-Host "DB connection string not provided; checking dotnet user-secrets..." -ForegroundColor Cyan
    try {
        if (Get-Command dotnet -ErrorAction SilentlyContinue) {
            $secretList = dotnet user-secrets list --project $ApiProject 2>$null
            if ($LASTEXITCODE -eq 0 -and $secretList) {
                $candidates = @('DB_CONNECTION_STRING','DbConnectionString','ConnectionStrings:DefaultConnection','ConnectionStrings:Default','Database:ConnectionString','ConnectionStrings__DefaultConnection','ConnectionStrings__Default')
                foreach ($k in $candidates) {
                    $pattern = '^\s*' + [regex]::Escape($k) + '\s*='
                    $match = $secretList | Where-Object { $_ -match $pattern } | Select-Object -First 1
                    if ($match) {
                        $idx = $match.IndexOf('=')
                        if ($idx -ge 0) {
                            $DbConnectionString = $match.Substring($idx+1).Trim()
                            break
                        }
                    }
                    # also try double-underscore form for hierarchical keys
                    $k2 = $k -replace ':','__'
                    $pattern2 = '^\s*' + [regex]::Escape($k2) + '\s*='
                    $match2 = $secretList | Where-Object { $_ -match $pattern2 } | Select-Object -First 1
                    if ($match2) {
                        $idx = $match2.IndexOf('=')
                        if ($idx -ge 0) {
                            $DbConnectionString = $match2.Substring($idx+1).Trim()
                            break
                        }
                    }
                }
            }
        }
    } catch {
    }

    if (-not $DbConnectionString) {
        Write-Host "Provide -DbConnectionString or set DB_CONNECTION_STRING env var." -ForegroundColor Yellow
        exit 1
    }
}

function Read-DotEnv($path) {
    $ht = @{}
    if (Test-Path $path) {
        foreach ($line in Get-Content $path -ErrorAction SilentlyContinue) {
            $line = $line.Trim()
            if ($line -eq '' -or $line.StartsWith('#')) { continue }
            $idx = $line.IndexOf('=')
            if ($idx -lt 1) { continue }
            $k = $line.Substring(0,$idx).Trim()
            $v = $line.Substring($idx+1).Trim()
            if ($v.StartsWith('"') -and $v.EndsWith('"')) { $v = $v.Substring(1,$v.Length-2) }
            if ($v.StartsWith("'") -and $v.EndsWith("'")) { $v = $v.Substring(1,$v.Length-2) }
            $ht[$k] = $v
        }
    }
    return $ht
}

$dotenv = Read-DotEnv $EnvFile

function Flush-ChildOutput {
    param(
        [pscustomobject]$Child
    )

    while ($Child.OutputTask -and $Child.OutputTask.IsCompleted) {
        $line = $Child.OutputTask.Result
        if ($null -eq $line) {
            $Child.OutputTask = $null
            break
        }

        if ($line -ne '') {
            Write-Host "[$($Child.Label)] $line"
        }

        $Child.OutputTask = $Child.Process.StandardOutput.ReadLineAsync()
    }

    while ($Child.ErrorTask -and $Child.ErrorTask.IsCompleted) {
        $line = $Child.ErrorTask.Result
        if ($null -eq $line) {
            $Child.ErrorTask = $null
            break
        }

        if ($line -ne '') {
            Write-Host "[$($Child.Label)] $line" -ForegroundColor Red
        }

        $Child.ErrorTask = $Child.Process.StandardError.ReadLineAsync()
    }
}

function Wait-ForChildren {
    param(
        [object[]]$Children
    )

    while ($true) {
        foreach ($child in $Children) {
            Flush-ChildOutput -Child $child
            $child.Process.Refresh()
        }

        if (@($Children | Where-Object { $_.Process.HasExited }).Count -gt 0) {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    while ([DateTime]::UtcNow -lt $deadline) {
        $madeProgress = $false

        foreach ($child in $Children) {
            $beforeOutputTask = $child.OutputTask
            $beforeErrorTask = $child.ErrorTask
            Flush-ChildOutput -Child $child

            if (($beforeOutputTask -ne $child.OutputTask) -or ($beforeErrorTask -ne $child.ErrorTask)) {
                $madeProgress = $true
            }
        }

        if (-not $madeProgress) {
            Start-Sleep -Milliseconds 50
        }
    }
}

function Start-ChildProcess {
    param(
        [string]$FileName,
        [string]$Arguments,
        [string]$WorkingDirectory,
        [hashtable]$Env = @{},
        [string]$Label = 'proc'
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo

    # On Windows calling scripts like 'npm' or 'npx' requires launching via cmd.exe
    $runningOnWindows = $false
    try { $runningOnWindows = $IsWindows } catch { $runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) }

    if ($runningOnWindows -and ($FileName -match '^(npm|npx|node)$')) {
        $psi.FileName = 'cmd.exe'
        # Use /c so the cmd process exits when the child completes
        $psi.Arguments = "/c $FileName $Arguments"
    }
    else {
        $psi.FileName = $FileName
        $psi.Arguments = $Arguments
    }

    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $false

    # inherit parent env, then apply overrides
    foreach ($kv in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $psi.Environment[$kv.Key] = $kv.Value
    }
    foreach ($kv in $Env.GetEnumerator()) {
        $psi.Environment[$kv.Key] = $kv.Value
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    # Log the start command so failures are visible immediately
    Write-Host "Starting $($Label): $($psi.FileName) $($psi.Arguments) (cwd=$WorkingDirectory)"

    if (-not $proc.Start()) { throw "Failed to start $Label ($psi.FileName $psi.Arguments)" }

    $child = [PSCustomObject]@{
        Process = $proc
        Label = $Label
        OutputTask = $proc.StandardOutput.ReadLineAsync()
        ErrorTask = $proc.StandardError.ReadLineAsync()
    }

    # If the child process exits immediately, surface any available output now.
    Start-Sleep -Milliseconds 250
    Flush-ChildOutput -Child $child

    if ($proc.HasExited) {
        Wait-ForChildren -Children @($child)
        throw "Child process $Label exited immediately with exit code $($proc.ExitCode)"
    }

    return $child
}

# Optionally install admin-ui deps (npm ci)
if (-not $SkipUiInstall) {
    if (-not (Test-Path (Join-Path $UiFolder 'node_modules'))) {
        Write-Host "Installing admin-ui dependencies (npm ci)..." -ForegroundColor Cyan
        $ciEnv = @{}
        foreach ($k in $dotenv.Keys) { $ciEnv[$k] = $dotenv[$k] }
        $ci = Start-ChildProcess -FileName 'npm' -Arguments 'ci' -WorkingDirectory $UiFolder -Env $ciEnv -Label 'npm-ci'
        Wait-ForChildren -Children @($ci)
        if ($ci.Process.ExitCode -ne 0) { Write-Host 'npm ci failed' -ForegroundColor Red; exit 1 }
    }
}

# Prepare child envs (pass .env values to children but do not mutate this shell)
$apiEnv = @{ 'DB_CONNECTION_STRING' = $DbConnectionString; 'ASPNETCORE_URLS' = "http://localhost:$BackendPort"; 'ASPNETCORE_ENVIRONMENT'='Development'; 'LOKI_URL' = '' }
foreach ($k in $dotenv.Keys) { if (-not $apiEnv.ContainsKey($k)) { $apiEnv[$k] = $dotenv[$k] } }

$uiEnv = @{}
foreach ($k in $dotenv.Keys) { $uiEnv[$k] = $dotenv[$k] }

Write-Host "Starting backend and admin UI in this terminal. Press Ctrl+C to stop both." -ForegroundColor Green

$apiArgs = "run --project `"$ApiProject`" --no-launch-profile"
$api = Start-ChildProcess -FileName 'dotnet' -Arguments $apiArgs -WorkingDirectory $ApiFolder -Env $apiEnv -Label 'API'
$ui  = Start-ChildProcess -FileName 'npm'   -Arguments 'run dev'            -WorkingDirectory $UiFolder  -Env $uiEnv  -Label 'UI'

$children = @($api, $ui)

try {
    Write-Host "API PID: $($api.Process.Id)" -ForegroundColor DarkGray
    Write-Host "UI  PID: $($ui.Process.Id)" -ForegroundColor DarkGray

    Wait-ForChildren -Children $children

    if ($api.Process.HasExited -and $api.Process.ExitCode -ne 0) {
        throw "API exited with code $($api.Process.ExitCode)"
    }

    if ($ui.Process.HasExited -and $ui.Process.ExitCode -ne 0) {
        throw "UI exited with code $($ui.Process.ExitCode)"
    }
}
finally {
    Write-Host "`nShutting down child processes..." -ForegroundColor Yellow
    foreach ($c in $children) {
        $p = $c.Process
        if ($p -and -not $p.HasExited) {
            try { $p.CloseMainWindow() | Out-Null } catch {}
            Start-Sleep -Seconds 1
            if (-not $p.HasExited) {
                try { $p.Kill($true) } catch { try { $p.Kill() } catch {} }
            }
        }
    }
}
