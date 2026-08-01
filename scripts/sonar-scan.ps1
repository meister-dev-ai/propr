#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Runs a local SonarQube analysis of the solution with test coverage imported.

.DESCRIPTION
    Clears stale coverage reports, runs the scanner begin step, builds and tests the
    solution with OpenCover collection, runs Vitest with an lcov report, and publishes
    the analysis with the scanner end step. All output goes to the console.

    A scan is one window, from begin to end, and the working tree has to hold still for it:

      - Nothing else may build these projects, an IDE background build included. The
        scanner captures every MSBuild build in the window, so a second build of the same
        project (a different configuration counts as a second build) leaves two sets of
        highlighting data for the same file, and the end step fails with "Cannot register
        highlighting rule ... as it overlaps at least one existing rule".
      - No editing or committing either. Roslyn reports issue positions against the code as
        it was compiled, while the end step indexes the files as they are on disk, so an
        edit in between fails the end step with "<n> is not a valid line offset for
        pointer" and misplaces coverage.

    Both are checked before the end step, so a broken scan is reported in seconds instead
    of after the upload.

.PARAMETER Token
    SonarQube authentication token. Defaults to $env:SONAR_TOKEN.

.PARAMETER HostUrl
    SonarQube host. Defaults to $env:SONAR_HOST_URL, else http://localhost:9000.

.PARAMETER Key
    Project key. Defaults to propr.

.PARAMETER Name
    Project name. Defaults to propr.

.PARAMETER Label
    Value for sonar.projectVersion. Defaults to local.

.PARAMETER CoverageDir
    Directory for the coverage reports. Defaults to .ignore/coverage in the repository.

.PARAMETER SkipBackend
    Skip the .NET build, tests and C# coverage.

.PARAMETER SkipFrontend
    Skip the Vitest run and the TypeScript/Vue coverage.

.PARAMETER EndOnly
    Publish an interrupted scan: run the end step alone, reusing the build output and
    coverage already on disk.

.EXAMPLE
    pwsh scripts/sonar-scan.ps1 <sonar-token>

.EXAMPLE
    pwsh scripts/sonar-scan.ps1 <sonar-token> -EndOnly

.NOTES
    Why the coverage flags are what they are:
      - coverlet.collector writes Cobertura by default, which the C# analyzer does not
        import. Format=opencover produces what sonar.cs.opencover.reportsPaths expects.
      - Vitest writes no coverage report at all when a test fails, unless reportOnFailure
        is set. A single failure would otherwise leave the frontend at 0 percent.
      - The v8 provider roughly doubles the frontend suite's wall time, so the default 5s
        per-test timeout starts to flake. This run raises it to 30s.
      - Reports go under .ignore/, which sonar.exclusions already skips. The scanner does
        not read .gitignore, so reports written elsewhere in the tree get indexed as
        source files even though git ignores them.

    The token is passed on the scanner command line and is therefore visible in the local
    process list for the duration of the scan.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Token = $env:SONAR_TOKEN,
    [string]$HostUrl = $env:SONAR_HOST_URL,
    [string]$Key = 'propr',
    [string]$Name = 'propr',
    [string]$Label = 'local',
    [string]$CoverageDir,
    [switch]$SkipBackend,
    [switch]$SkipFrontend,
    [switch]$EndOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Test failures are handled below so the analysis still gets published, so native
# command exit codes must not terminate the script on their own.
if (Test-Path 'variable:PSNativeCommandUseErrorActionPreference') {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ScriptDir
$BuildConfig = 'Release'
$ScannerOut = Join-Path $RepoRoot '.sonarqube/out'
$SourceState = Join-Path $RepoRoot '.sonarqube/propr-source-state'

function Write-Step {
    param([string]$Title)

    Write-Host ""
    Write-Host "=== $Title ==="
}

function Write-Fail {
    param([string]$Message)

    [Console]::Error.WriteLine($Message)
}

# Two analyses of one project leave two sets of highlighting data for the same source
# file, and the end step throws in the protobuf importer when it imports the second.
# Catching it here costs seconds; letting the end step run costs the whole upload.
function Assert-SingleAnalysisPerProject {
    if (-not (Test-Path -LiteralPath $ScannerOut)) {
        return
    }

    $analyses = @(
        Get-ChildItem -LiteralPath $ScannerOut -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $info = Join-Path $_.FullName 'ProjectInfo.xml'
            if (Test-Path -LiteralPath $info -PathType Leaf) {
                $text = Get-Content -LiteralPath $info -Raw
                $nameMatch = [regex]::Match($text, '<ProjectName>([^<]*)</ProjectName>')
                $configMatch = [regex]::Match($text, '<Configuration>([^<]*)</Configuration>')
                if ($nameMatch.Success) {
                    [pscustomobject]@{
                        ProjectName   = $nameMatch.Groups[1].Value
                        Configuration = if ($configMatch.Success) { $configMatch.Groups[1].Value } else { 'unknown' }
                        Directory     = $_.FullName
                    }
                }
            }
        }
    )

    $duplicateNames = @($analyses | Group-Object -Property ProjectName |
            Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($duplicateNames.Count -eq 0) {
        return
    }

    Write-Fail 'Some projects were analysed more than once in this scan:'
    Write-Fail ''

    $stray = [System.Collections.Generic.List[string]]::new()
    foreach ($duplicate in ($duplicateNames | Sort-Object)) {
        foreach ($analysis in ($analyses | Where-Object { $_.ProjectName -eq $duplicate })) {
            Write-Fail ('  {0,-46} {1,-8} {2}' -f $analysis.ProjectName, $analysis.Configuration, $analysis.Directory)
            if ($analysis.Configuration -ne $BuildConfig) {
                $stray.Add($analysis.Directory)
            }
        }
    }

    Write-Fail ''
    Write-Fail 'The end step would fail while importing highlighting for a file that appears in'
    Write-Fail 'two of these. Something built those projects while the scan was running, an IDE'
    Write-Fail 'background build included.'

    if ($stray.Count -gt 0) {
        Write-Fail ''
        Write-Fail "The ones above that are not $BuildConfig were not built by this script. Remove them:"
        Write-Fail ('  Remove-Item -Recurse -Force ' + (($stray | ForEach-Object { "'$_'" }) -join ', '))
    }
    else {
        Write-Fail ''
        Write-Fail 'Remove the duplicate directories, keeping one analysis per project.'
    }

    Write-Fail 'Then publish without rebuilding:'
    Write-Fail "  pwsh $PSCommandPath <token> -EndOnly"
    exit 1
}

function Test-IsGitRepository {
    git -C $RepoRoot rev-parse --git-dir 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

# Identifies the committed and uncommitted state of the tracked sources. Untracked files
# are left out, so a scratch file appearing mid-scan does not read as a source change.
function Get-SourceFingerprint {
    $parts = @(
        (git -C $RepoRoot rev-parse HEAD 2>&1)
        (git -C $RepoRoot status --porcelain --untracked-files=no 2>&1)
        (git -C $RepoRoot diff HEAD 2>&1)
    )
    $text = (($parts | ForEach-Object { $_ -join "`n" }) -join "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '')
}

# Roslyn reported its issue positions against the code as it was compiled. If the sources
# moved on since, the end step reads positions that no longer exist.
function Assert-SourceUnchanged {
    if (-not (Test-IsGitRepository)) {
        return
    }

    if (-not (Test-Path -LiteralPath $SourceState -PathType Leaf)) {
        Write-Host 'No source state was recorded at begin, so the sources cannot be compared.'
        return
    }

    $recorded = @(Get-Content -LiteralPath $SourceState)

    # The bash twin hashes the same facts a different way, so its fingerprint is not
    # comparable with this one.
    if ($recorded.Count -lt 3 -or $recorded[2] -ne 'ps1') {
        Write-Host 'Source state was recorded by sonar-scan.sh; skipping the comparison.'
        return
    }

    $expectedHash = if ($recorded.Count -gt 0) { $recorded[0] } else { '' }
    $expectedHead = if ($recorded.Count -gt 1) { $recorded[1] } else { '' }
    $actualHash = Get-SourceFingerprint
    $actualHead = git -C $RepoRoot rev-parse HEAD 2>&1

    if ($expectedHash -eq $actualHash) {
        Write-Host 'Sources unchanged since the build.'
        return
    }

    Write-Fail 'The sources changed after the build.'
    Write-Fail ''
    if ($expectedHead -ne $actualHead) {
        Write-Fail "  HEAD at begin: $expectedHead"
        Write-Fail "  HEAD now:      $actualHead"
    }
    git -C $RepoRoot status --short --untracked-files=no | ForEach-Object { Write-Fail $_ }
    Write-Fail ''
    Write-Fail 'Roslyn reported issue positions against the code it compiled, and the end step'
    Write-Fail 'indexes the files as they are now, so it would fail with "is not a valid line'
    Write-Fail 'offset for pointer" or place issues and coverage on the wrong lines.'
    Write-Fail ''
    Write-Fail 'Re-run the whole scan and leave the tree alone until it finishes. -EndOnly'
    Write-Fail 'cannot help here: the Roslyn reports have to be regenerated.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($HostUrl)) {
    $HostUrl = 'http://localhost:9000'
}

if ([string]::IsNullOrWhiteSpace($CoverageDir)) {
    $CoverageDir = Join-Path $RepoRoot '.ignore/coverage'
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Fail 'No SonarQube token. Pass it as the first argument or set SONAR_TOKEN.'
    Write-Fail 'Run "Get-Help scripts/sonar-scan.ps1 -Detailed" for the full usage.'
    exit 2
}

if ($SkipBackend -and $SkipFrontend -and -not $EndOnly) {
    Write-Fail 'Nothing to do: both -SkipBackend and -SkipFrontend were given.'
    exit 2
}

if (-not (Get-Command 'dotnet-sonarscanner' -ErrorAction SilentlyContinue)) {
    Write-Fail 'dotnet-sonarscanner not found. Install it with:'
    Write-Fail '  dotnet tool install --global dotnet-sonarscanner'
    exit 1
}

$Settings = Join-Path $RepoRoot 'SonarQube.Analysis.xml'
$FrontendRoot = Join-Path $RepoRoot 'frontend'

if ($EndOnly) {
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.sonarqube/conf/SonarQubeAnalysisConfig.xml') -PathType Leaf)) {
        Write-Fail "No scan to finish: $RepoRoot/.sonarqube holds no begin-step output."
        Write-Fail 'Run the script without -EndOnly to scan from the start.'
        exit 1
    }
}
else {
    if (-not (Test-Path -LiteralPath $Settings -PathType Leaf)) {
        Write-Fail "Missing $Settings."
        Write-Fail 'It carries sonar.exclusions. Without it the scan indexes nested worktrees and'
        Write-Fail 'local .env files, and the server keeps a copy of everything it indexes.'
        exit 1
    }

    if (-not $SkipFrontend -and -not (Test-Path -LiteralPath (Join-Path $FrontendRoot 'node_modules'))) {
        Write-Fail 'frontend/node_modules is missing. Run "npm ci --prefix frontend" first, or'
        Write-Fail 'pass -SkipFrontend to scan the backend only.'
        exit 1
    }
}

$BackendCoverage = Join-Path $CoverageDir 'backend'
$FrontendCoverage = Join-Path $CoverageDir 'frontend'

if (-not $EndOnly) {
    # Stale reports from an earlier run would be imported as if they were current.
    Write-Step 'Clearing previous coverage reports'
    if (-not $SkipBackend -and (Test-Path -LiteralPath $BackendCoverage)) {
        Remove-Item -LiteralPath $BackendCoverage -Recurse -Force
    }
    if (-not $SkipFrontend -and (Test-Path -LiteralPath $FrontendCoverage)) {
        Remove-Item -LiteralPath $FrontendCoverage -Recurse -Force
    }
    New-Item -ItemType Directory -Path $CoverageDir -Force | Out-Null
    Write-Host "Coverage directory: $CoverageDir"

    $beginArgs = @(
        "/k:$Key"
        "/n:$Name"
        "/v:$Label"
        "/s:$Settings"
        "/d:sonar.host.url=$HostUrl"
        "/d:sonar.token=$Token"
    )
    if (-not $SkipBackend) {
        $beginArgs += "/d:sonar.cs.opencover.reportsPaths=$BackendCoverage/**/coverage.opencover.xml"
    }
    if (-not $SkipFrontend) {
        $beginArgs += "/d:sonar.javascript.lcov.reportPaths=$FrontendCoverage/lcov.info"
    }

    Write-Step 'Scanner begin'
    Write-Host 'Do not build these projects until the scan finishes, in an IDE or elsewhere.'
    Write-Host ""
    dotnet sonarscanner begin @beginArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Fail ''
        Write-Fail 'Scanner begin failed. Nothing was analysed.'
        exit 1
    }

    # Recorded now so the check before the end step can tell whether the sources moved on.
    if (Test-IsGitRepository) {
        @(
            (Get-SourceFingerprint)
            (git -C $RepoRoot rev-parse HEAD 2>&1)
            'ps1'
        ) | Set-Content -LiteralPath $SourceState
    }
}

$testsFailed = $false
$Solution = Join-Path $RepoRoot 'MeisterDev.ProPR.slnx'

if (-not $EndOnly -and -not $SkipBackend) {
    # The build has to happen between begin and end, so the scanner's targets can collect
    # the Roslyn analyzer output for every project.
    Write-Step 'dotnet build'
    dotnet build $Solution --configuration $BuildConfig --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail ''
        Write-Fail 'Build failed. Skipping the rest of the scan: an analysis without analyzer'
        Write-Fail 'output for the failed projects would report misleading results.'
        exit 1
    }

    Write-Step 'dotnet test with coverage'
    dotnet test $Solution --configuration $BuildConfig --no-build --nologo `
        '--collect:XPlat Code Coverage;Format=opencover' `
        --results-directory $BackendCoverage
    if ($LASTEXITCODE -ne 0) {
        Write-Fail ''
        Write-Fail 'Backend tests failed. Continuing so the analysis still gets published.'
        $testsFailed = $true
    }
}

if (-not $EndOnly -and -not $SkipFrontend) {
    Write-Step 'Vitest with coverage'
    Push-Location $FrontendRoot
    try {
        npx vitest run --coverage `
            --coverage.reporter=lcov `
            --coverage.reporter=text-summary `
            --coverage.reportOnFailure `
            "--coverage.reportsDirectory=$FrontendCoverage" `
            --testTimeout=30000
        $frontendExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($frontendExit -ne 0) {
        Write-Fail ''
        Write-Fail 'Frontend tests failed. Continuing so the analysis still gets published.'
        $testsFailed = $true
    }
}

Write-Step 'Checking the scanner output'
Assert-SingleAnalysisPerProject
Write-Host 'One analysis per project.'
Assert-SourceUnchanged

if ($EndOnly) {
    Write-Host 'Coverage comes from the paths the earlier begin step recorded.'
}
else {
    if (-not $SkipBackend) {
        $backendReports = @(Get-ChildItem -LiteralPath $BackendCoverage -Filter 'coverage.opencover.xml' `
                -Recurse -File -ErrorAction SilentlyContinue).Count
        Write-Host "C# OpenCover reports: $backendReports"
        if ($backendReports -eq 0) {
            Write-Fail 'No C# coverage was produced, so C# coverage will be reported as 0 percent.'
        }
    }
    if (-not $SkipFrontend) {
        $lcov = Join-Path $FrontendCoverage 'lcov.info'
        if (Test-Path -LiteralPath $lcov -PathType Leaf) {
            Write-Host "TypeScript lcov report: $lcov"
        }
        else {
            Write-Fail 'No lcov.info was produced, so frontend coverage will be reported as 0 percent.'
        }
    }
}

# The end step must be given the token and nothing else.
Write-Step 'Scanner end'
dotnet sonarscanner end "/d:sonar.token=$Token"
if ($LASTEXITCODE -ne 0) {
    Write-Fail ''
    Write-Fail 'Scanner end failed. Nothing was published.'
    exit 1
}

Write-Host ""
Write-Host "Analysis published: $($HostUrl.TrimEnd('/'))/dashboard?id=$Key"

if ($testsFailed) {
    Write-Fail 'One or more test suites failed. Coverage above is from an incomplete run.'
    exit 1
}
