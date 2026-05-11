Param(
    [string]$DbConnectionString = $env:DB_CONNECTION_STRING,
    [int]$BackendPort = 8080,
    [int]$ProCursorPort = 8081,
    [switch]$SkipUiInstall,
    [string]$ProCursorDbConnectionString = $env:PROCURSOR_DB_CONNECTION_STRING
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ApiProject = Join-Path $RepoRoot 'src\MeisterProPR.Api\MeisterProPR.Api.csproj'
$ProCursorProject = Join-Path $RepoRoot 'src\MeisterProPR.ProCursor.Service\MeisterProPR.ProCursor.Service.csproj'
$ApiFolder = Join-Path $RepoRoot 'src\MeisterProPR.Api'
$ProCursorFolder = Join-Path $RepoRoot 'src\MeisterProPR.ProCursor.Service'
$UiFolder = Join-Path $RepoRoot 'admin-ui'
$EnvFile = Join-Path $RepoRoot '.env'
$ApiDll = Join-Path $RepoRoot 'src\MeisterProPR.Api\bin\Debug\net10.0\MeisterProPR.Api.dll'
$ProCursorDll = Join-Path $RepoRoot 'src\MeisterProPR.ProCursor.Service\bin\Debug\net10.0\MeisterProPR.ProCursor.Service.dll'
$LogDir = if ($env:RUN_LOCAL_LOG_DIR) { $env:RUN_LOCAL_LOG_DIR } else { Join-Path $RepoRoot 'logs\local' }
$LogFile = if ($env:RUN_LOCAL_LOG_FILE) { $env:RUN_LOCAL_LOG_FILE } else { Join-Path $LogDir ("run-local-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$runningOnWindows = $false
try {
    $runningOnWindows = $IsWindows
}
catch {
    $runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

$LegacyDataProtectionKeysDir = Join-Path (Join-Path $HOME '.aspnet') 'DataProtection-Keys'
$DefaultDataProtectionKeysDir = if ($runningOnWindows -and $env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA 'ASP.NET\DataProtection-Keys'
}
else {
    $LegacyDataProtectionKeysDir
}

$DataProtectionKeysDir = if ($env:RUN_LOCAL_KEYS_DIR) { $env:RUN_LOCAL_KEYS_DIR } else { $DefaultDataProtectionKeysDir }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null
New-Item -ItemType Directory -Force -Path $DataProtectionKeysDir | Out-Null

if (-not $env:RUN_LOCAL_KEYS_DIR -and
    $runningOnWindows -and
    (Test-Path $LegacyDataProtectionKeysDir) -and
    ([System.IO.Path]::GetFullPath($LegacyDataProtectionKeysDir) -ne [System.IO.Path]::GetFullPath($DataProtectionKeysDir))) {
    foreach ($legacyKey in Get-ChildItem -Path $LegacyDataProtectionKeysDir -Filter 'key-*.xml' -File -ErrorAction SilentlyContinue) {
        $targetKeyPath = Join-Path $DataProtectionKeysDir $legacyKey.Name
        if (-not (Test-Path $targetKeyPath)) {
            Copy-Item -Path $legacyKey.FullName -Destination $targetKeyPath
        }
    }
}

Set-Content -Path $LogFile -Value $null -Encoding utf8

$script:UserSecretsList = $null

function Write-RunLocalMessage {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $formatted = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    Add-Content -Path $script:LogFile -Value $formatted -Encoding utf8
    Write-Host $formatted -ForegroundColor $Color
}

function Get-ConnectionTarget {
    param([string]$ConnectionString)

    $hostName = $null
    $port = $null
    $database = $null

    foreach ($segment in ($ConnectionString -split ';')) {
        $trimmed = $segment.Trim()
        if (-not $trimmed) {
            continue
        }

        $parts = $trimmed -split '=', 2
        if ($parts.Count -ne 2) {
            continue
        }

        $key = $parts[0].Trim().ToLowerInvariant()
        $value = $parts[1].Trim()

        switch ($key) {
            'host' { $hostName = $value }
            'server' { $hostName = $value }
            'data source' { $hostName = $value }
            'port' { $port = $value }
            'database' { $database = $value }
            'initial catalog' { $database = $value }
        }
    }

    if (-not $hostName) { $hostName = '<unspecified-host>' }
    if (-not $port) { $port = '<default-port>' }
    if (-not $database) { $database = '<unspecified-db>' }

    return "host=$hostName port=$port database=$database"
}

function Get-UserSecretsList {
    if ($null -ne $script:UserSecretsList) {
        return $script:UserSecretsList
    }

    $script:UserSecretsList = @()

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (Test-Path $ApiProject)) {
        return $script:UserSecretsList
    }

    try {
        $secretList = dotnet user-secrets list --project $ApiProject 2>$null
        if ($LASTEXITCODE -eq 0 -and $secretList) {
            $script:UserSecretsList = @($secretList)
        }
    }
    catch {
    }

    return $script:UserSecretsList
}

function Get-UserSecretValue {
    param([string]$WantedKey)

    foreach ($line in (Get-UserSecretsList)) {
        if ($null -eq $line) {
            continue
        }

        $trimmed = $line.Trim()
        if (-not $trimmed) {
            continue
        }

        $idx = $trimmed.IndexOf('=')
        if ($idx -lt 0) {
            continue
        }

        $key = $trimmed.Substring(0, $idx).Trim()
        $value = $trimmed.Substring($idx + 1).Trim()
        if ($key -eq $WantedKey) {
            return $value
        }
    }

    return $null
}

function Get-DbConnectionStringFromUserSecrets {
    $candidates = @(
        'DB_CONNECTION_STRING',
        'DbConnectionString',
        'ConnectionStrings:DefaultConnection',
        'ConnectionStrings:Default',
        'Database:ConnectionString',
        'ConnectionStrings__DefaultConnection',
        'ConnectionStrings__Default'
    )

    $candidateLookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        [void]$candidateLookup.Add($candidate)
        [void]$candidateLookup.Add(($candidate -replace ':', '__'))
    }

    foreach ($line in (Get-UserSecretsList)) {
        if ($null -eq $line) {
            continue
        }

        $trimmed = $line.Trim()
        if (-not $trimmed) {
            continue
        }

        $idx = $trimmed.IndexOf('=')
        if ($idx -lt 0) {
            continue
        }

        $key = $trimmed.Substring(0, $idx).Trim()
        $value = $trimmed.Substring($idx + 1).Trim()
        if ($candidateLookup.Contains($key)) {
            return $value
        }
    }

    return $null
}

function Get-DbConnectionFromLocalPodman {
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
        return $null
    }

    $candidates = @(
        'meisterpropr-pgvector-db',
        'docker-compose-postgres-1'
    )

    foreach ($containerName in $candidates) {
        try {
            $inspect = podman inspect $containerName 2>$null | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or -not $inspect) {
                continue
            }

            $container = if ($inspect -is [array]) { $inspect[0] } else { $inspect }
            if ($container.State.Status -ne 'running') {
                continue
            }

            $bindings = $container.NetworkSettings.Ports.'5432/tcp'
            if (-not $bindings -or $bindings.Count -lt 1) {
                continue
            }

            $hostPort = $bindings[0].HostPort
            if (-not $hostPort) {
                continue
            }

            $envMap = @{}
            foreach ($entry in $container.Config.Env) {
                $parts = $entry -split '=', 2
                if ($parts.Count -eq 2) {
                    $envMap[$parts[0]] = $parts[1]
                }
            }

            $username = if ($envMap.ContainsKey('POSTGRES_USER') -and $envMap['POSTGRES_USER']) { $envMap['POSTGRES_USER'] } else { 'postgres' }
            $password = if ($envMap.ContainsKey('POSTGRES_PASSWORD')) { $envMap['POSTGRES_PASSWORD'] } else { $null }
            $database = if ($envMap.ContainsKey('POSTGRES_DB') -and $envMap['POSTGRES_DB']) { $envMap['POSTGRES_DB'] } else { 'postgres' }

            if (-not $password) {
                continue
            }

            return [PSCustomObject]@{
                ContainerName = $containerName
                ConnectionString = "Host=localhost;Port=$hostPort;Database=$database;Username=$username;Password=$password"
            }
        }
        catch {
        }
    }

    return $null
}

function Read-DotEnv {
    param([string]$Path)

    $ht = @{}
    if (Test-Path $Path) {
        foreach ($line in Get-Content $Path -ErrorAction SilentlyContinue) {
            $trimmed = $line.Trim()
            if ($trimmed -eq '' -or $trimmed.StartsWith('#')) {
                continue
            }

            $idx = $trimmed.IndexOf('=')
            if ($idx -lt 1) {
                continue
            }

            $key = $trimmed.Substring(0, $idx).Trim()
            $value = $trimmed.Substring($idx + 1).Trim()
            if ($value.StartsWith('"') -and $value.EndsWith('"')) { $value = $value.Substring(1, $value.Length - 2) }
            if ($value.StartsWith("'") -and $value.EndsWith("'")) { $value = $value.Substring(1, $value.Length - 2) }
            $ht[$key] = $value
        }
    }

    return $ht
}

function Add-DotEnvValues {
    param(
        [hashtable]$Target,
        [hashtable]$DotEnv,
        [string[]]$ExcludedKeys = @()
    )

    $excluded = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $ExcludedKeys) {
        [void]$excluded.Add($key)
    }

    foreach ($key in $DotEnv.Keys) {
        if (-not $excluded.Contains([string]$key)) {
            $Target[$key] = $DotEnv[$key]
        }
    }
}

function New-ProCursorSharedKey {
    if ($env:PROCURSOR_SHARED_KEY) {
        return $env:PROCURSOR_SHARED_KEY
    }

    $bytes = [byte[]]::new(32)
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    }
    finally {
        if ($null -ne $random) {
            $random.Dispose()
        }
    }

    return (-join ($bytes | ForEach-Object { $_.ToString('x2') }))
}

function Test-ChildStructuredLogLine {
    param([string]$Line)

    return $Line -match '^\[\d{2}:\d{2}:\d{2} [A-Z]{3}\]'
}

function Format-ChildOutputLine {
    param(
        [pscustomobject]$Child,
        [string]$Line
    )

    if (Test-ChildStructuredLogLine -Line $Line) {
        $Child.InDbCommandBlock = $Line.Contains('DbCommand (')
        return "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$($Child.Label)] $Line"
    }

    if ($Child.InDbCommandBlock) {
        return "                    [$($Child.Label)] | $Line"
    }

    return "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$($Child.Label)] $Line"
}

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
            $formatted = Format-ChildOutputLine -Child $Child -Line $line
            Add-Content -Path $script:LogFile -Value $formatted -Encoding utf8
            Write-Host $formatted -ForegroundColor Gray
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
            $formatted = Format-ChildOutputLine -Child $Child -Line $line
            Add-Content -Path $script:LogFile -Value $formatted -Encoding utf8
            Write-Host $formatted -ForegroundColor Red
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

function Stop-Children {
    param([object[]]$Children)

    foreach ($child in $Children) {
        if (-not $child) {
            continue
        }

        $process = $child.Process
        if ($process -and -not $process.HasExited) {
            try { $process.CloseMainWindow() | Out-Null } catch {}
            Start-Sleep -Milliseconds 250
            if (-not $process.HasExited) {
                try { $process.Kill($true) } catch { try { $process.Kill() } catch {} }
            }
        }
    }

    foreach ($child in $Children) {
        if ($child) {
            try { Wait-ForChildren -Children @($child) } catch {}
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
    $runningOnWindows = $false
    try {
        $runningOnWindows = $IsWindows
    }
    catch {
        $runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    }

    if ($runningOnWindows -and ($FileName -match '^(npm|npx|node)$')) {
        $psi.FileName = 'cmd.exe'
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

    foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $psi.Environment[$entry.Key] = $entry.Value
    }

    foreach ($entry in $Env.GetEnumerator()) {
        $psi.Environment[$entry.Key] = $entry.Value
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    Write-RunLocalMessage -Message "Starting ${Label}: $($psi.FileName) $($psi.Arguments) (cwd=$WorkingDirectory)"

    if (-not $proc.Start()) {
        throw "Failed to start $Label ($($psi.FileName) $($psi.Arguments))"
    }

    $child = [PSCustomObject]@{
        Process = $proc
        Label = $Label
        OutputTask = $proc.StandardOutput.ReadLineAsync()
        ErrorTask = $proc.StandardError.ReadLineAsync()
        InDbCommandBlock = $false
    }

    Start-Sleep -Milliseconds 250
    Flush-ChildOutput -Child $child

    if ($proc.HasExited) {
        Wait-ForChildren -Children @($child)
        throw "Child process $Label exited immediately with exit code $($proc.ExitCode)"
    }

    return $child
}

function Invoke-LoggedCommand {
    param(
        [string]$FileName,
        [string]$Arguments,
        [string]$WorkingDirectory,
        [hashtable]$Env = @{},
        [string]$Label
    )

    $child = Start-ChildProcess -FileName $FileName -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Env $Env -Label $Label
    Wait-ForChildren -Children @($child)
    if ($child.Process.ExitCode -ne 0) {
        throw "$Label exited with code $($child.Process.ExitCode)"
    }
}

function Test-HttpReady {
    param([string]$Url)

    try {
        $request = [System.Net.WebRequest]::Create($Url)
        $request.Method = 'GET'
        $request.Timeout = 2000

        if ($request -is [System.Net.HttpWebRequest]) {
            $request.AllowAutoRedirect = $false
            $request.Proxy = $null
            $request.KeepAlive = $false
            $request.ReadWriteTimeout = 2000
        }

        $response = $request.GetResponse()
        try {
            return $response -is [System.Net.HttpWebResponse] -and ([int]$response.StatusCode -ge 200) -and ([int]$response.StatusCode -lt 300)
        }
        finally {
            $response.Close()
        }
    }
    catch {
        return $false
    }
}

function Wait-ForHttpReady {
    param(
        [string]$Name,
        [int]$Port,
        [pscustomobject]$Child,
        [string]$Path = '/livez',
        [int]$TimeoutSeconds = 60
    )

    $normalizedPath = if ($Path.StartsWith('/')) { $Path } else { "/$Path" }
    $readyUrl = "http://127.0.0.1:$Port$normalizedPath"
    Write-RunLocalMessage -Message "Waiting for $Name readiness at $readyUrl" -Color Cyan

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Flush-ChildOutput -Child $Child
        $Child.Process.Refresh()

        if (Test-HttpReady -Url $readyUrl) {
            Write-RunLocalMessage -Message "$Name is ready on http://127.0.0.1:$Port" -Color Green
            return $true
        }

        if ($Child.Process.HasExited) {
            Wait-ForChildren -Children @($Child)
            Write-RunLocalMessage -Message "$Name exited before becoming ready" -Color Red
            return $false
        }

        Start-Sleep -Seconds 1
    }

    Write-RunLocalMessage -Message "Timed out waiting for $Name readiness after ${TimeoutSeconds}s" -Color Red
    return $false
}

if (-not $DbConnectionString) {
    $podmanDb = Get-DbConnectionFromLocalPodman
    if ($podmanDb) {
        $DbConnectionString = $podmanDb.ConnectionString
        Write-RunLocalMessage -Message "DB connection not provided; using running local Podman PostgreSQL container '$($podmanDb.ContainerName)' ($(Get-ConnectionTarget -ConnectionString $DbConnectionString))" -Color Cyan
    }
}

if (-not $DbConnectionString) {
    Write-RunLocalMessage -Message 'DB connection string not provided; checking dotnet user-secrets' -Color Cyan
    $DbConnectionString = Get-DbConnectionStringFromUserSecrets
}

if (-not $DbConnectionString) {
    Write-RunLocalMessage -Message 'Provide -DbConnectionString or set DB_CONNECTION_STRING env var.' -Color Yellow
    exit 1
}

if (-not $ProCursorDbConnectionString) {
    $ProCursorDbConnectionString = $DbConnectionString
}

$apiDbTarget = Get-ConnectionTarget -ConnectionString $DbConnectionString
$proCursorDbTarget = Get-ConnectionTarget -ConnectionString $ProCursorDbConnectionString
if ($DbConnectionString -eq $ProCursorDbConnectionString) {
    Write-RunLocalMessage -Message "Using shared local PostgreSQL target for ProPR and ProCursor ($apiDbTarget)" -Color Cyan
}
else {
    Write-RunLocalMessage -Message "Using ProPR PostgreSQL target ($apiDbTarget)" -Color Cyan
    Write-RunLocalMessage -Message "Using ProCursor PostgreSQL target ($proCursorDbTarget)" -Color Cyan
}

Write-RunLocalMessage -Message "Using data protection key ring at $DataProtectionKeysDir" -Color DarkGray

$dotenv = Read-DotEnv -Path $EnvFile
if (-not $dotenv.ContainsKey('MEISTER_JWT_SECRET') -and -not $env:MEISTER_JWT_SECRET) {
    $userSecretJwt = Get-UserSecretValue -WantedKey 'MEISTER_JWT_SECRET'
    if ($userSecretJwt) {
        $dotenv['MEISTER_JWT_SECRET'] = $userSecretJwt
    }
}

$proCursorSharedKey = New-ProCursorSharedKey

$apiEnv = @{
    'DB_CONNECTION_STRING' = $DbConnectionString
    'ASPNETCORE_URLS' = "http://0.0.0.0:$BackendPort"
    'ASPNETCORE_ENVIRONMENT' = 'Development'
    'LOKI_URL' = ''
    'PROCURSOR_REMOTE_MODE' = 'proprManagedRemote'
    'PROCURSOR_SERVICE_BASE_URL' = "http://127.0.0.1:$ProCursorPort"
    'PROCURSOR_SHARED_KEY' = $proCursorSharedKey
    'MEISTER_DATA_PROTECTION_KEYS_PATH' = $DataProtectionKeysDir
}
Add-DotEnvValues -Target $apiEnv -DotEnv $dotenv -ExcludedKeys @(
    'DB_CONNECTION_STRING',
    'PROCURSOR_DB_CONNECTION_STRING',
    'ASPNETCORE_URLS',
    'ASPNETCORE_ENVIRONMENT',
    'LOKI_URL',
    'PROCURSOR_REMOTE_MODE',
    'PROCURSOR_SERVICE_BASE_URL',
    'PROCURSOR_SHARED_KEY',
    'MEISTER_DATA_PROTECTION_KEYS_PATH'
)

$proCursorEnv = @{
    'ASPNETCORE_URLS' = "http://0.0.0.0:$ProCursorPort"
    'ASPNETCORE_ENVIRONMENT' = 'Development'
    'LOKI_URL' = ''
    'PROCURSOR_PROPR_BASE_URL' = "http://127.0.0.1:$BackendPort"
    'PROCURSOR_DB_CONNECTION_STRING' = $ProCursorDbConnectionString
    'PROCURSOR_SHARED_KEY' = $proCursorSharedKey
    'MEISTER_DATA_PROTECTION_KEYS_PATH' = $DataProtectionKeysDir
}
Add-DotEnvValues -Target $proCursorEnv -DotEnv $dotenv -ExcludedKeys @(
    'DB_CONNECTION_STRING',
    'PROCURSOR_DB_CONNECTION_STRING',
    'ASPNETCORE_URLS',
    'ASPNETCORE_ENVIRONMENT',
    'LOKI_URL',
    'PROCURSOR_PROPR_BASE_URL',
    'PROCURSOR_SHARED_KEY',
    'MEISTER_DATA_PROTECTION_KEYS_PATH'
)

$uiEnv = @{
    'VITE_API_BASE_URL' = '/api'
}
Add-DotEnvValues -Target $uiEnv -DotEnv $dotenv -ExcludedKeys @('VITE_API_BASE_URL')

if (-not $SkipUiInstall) {
    if (-not (Test-Path (Join-Path $UiFolder 'node_modules'))) {
        Write-RunLocalMessage -Message 'Installing admin-ui dependencies (npm ci)' -Color Cyan
        Invoke-LoggedCommand -FileName 'npm' -Arguments 'ci' -WorkingDirectory $UiFolder -Env $uiEnv -Label 'npm-ci'
    }
}

Write-RunLocalMessage -Message 'Building backend and ProCursor host' -Color Cyan
Invoke-LoggedCommand -FileName 'dotnet' -Arguments "build `"$ApiProject`"" -WorkingDirectory $RepoRoot -Label 'build-api'
Invoke-LoggedCommand -FileName 'dotnet' -Arguments "build `"$ProCursorProject`"" -WorkingDirectory $RepoRoot -Label 'build-procursor'

$api = $null
$proCursor = $null
$ui = $null

try {
    Write-RunLocalMessage -Message 'Starting backend, ProCursor, and admin UI in this terminal. Press Ctrl+C to stop all processes.' -Color Green
    Write-RunLocalMessage -Message "Local run log: $LogFile" -Color DarkGray
    Write-RunLocalMessage -Message 'Shared ProCursor key generated for this run' -Color DarkGray

    $api = Start-ChildProcess -FileName 'dotnet' -Arguments "`"$ApiDll`"" -WorkingDirectory $ApiFolder -Env $apiEnv -Label 'API'
    if (-not (Wait-ForHttpReady -Name 'API' -Port $BackendPort -Child $api -TimeoutSeconds 60)) {
        throw 'API failed to become ready'
    }

    $proCursor = Start-ChildProcess -FileName 'dotnet' -Arguments "`"$ProCursorDll`"" -WorkingDirectory $ProCursorFolder -Env $proCursorEnv -Label 'PROCURSOR'
    if (-not (Wait-ForHttpReady -Name 'ProCursor' -Port $ProCursorPort -Child $proCursor -TimeoutSeconds 60)) {
        throw 'ProCursor failed to become ready'
    }

    $ui = Start-ChildProcess -FileName 'npm' -Arguments 'run dev' -WorkingDirectory $UiFolder -Env $uiEnv -Label 'UI'

    $children = @($api, $proCursor, $ui)

    Write-RunLocalMessage -Message "API PID: $($api.Process.Id)" -Color DarkGray
    Write-RunLocalMessage -Message "ProCursor PID: $($proCursor.Process.Id)" -Color DarkGray
    Write-RunLocalMessage -Message "UI PID: $($ui.Process.Id)" -Color DarkGray
    Write-RunLocalMessage -Message "API -> http://localhost:$BackendPort  ProCursor -> http://localhost:$ProCursorPort  Admin UI -> http://localhost:5173" -Color Green

    Wait-ForChildren -Children $children

    if ($api.Process.HasExited -and $api.Process.ExitCode -ne 0) {
        throw "API exited with code $($api.Process.ExitCode)"
    }

    if ($proCursor.Process.HasExited -and $proCursor.Process.ExitCode -ne 0) {
        throw "ProCursor exited with code $($proCursor.Process.ExitCode)"
    }

    if ($ui.Process.HasExited -and $ui.Process.ExitCode -ne 0) {
        throw "UI exited with code $($ui.Process.ExitCode)"
    }
}
finally {
    Write-RunLocalMessage -Message 'Shutting down child processes' -Color Yellow
    Stop-Children -Children @($api, $proCursor, $ui)
}
