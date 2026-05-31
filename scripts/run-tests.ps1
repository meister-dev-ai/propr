$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

$Failed = $false

function Invoke-Quiet {
    param([string]$Label, [scriptblock]$Command)

    Write-Host "Running $Label..."
    $output = & $Command 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "[$Label] FAILED (exit code $LASTEXITCODE):" -ForegroundColor Red
        $output | ForEach-Object { Write-Host "$_" -ForegroundColor Red }
        $script:Failed = $true
    }
}

Invoke-Quiet -Label "dotnet test" -Command { dotnet test "$RepoRoot/MeisterProPR.slnx" --verbosity quiet }
Invoke-Quiet -Label "npm test" -Command { npm test --prefix "$RepoRoot/frontend" }

if ($Failed) {
    Write-Host ""
    Write-Host "One or more test suites failed." -ForegroundColor Red
    exit 1
}

Write-Host "All tests passed." -ForegroundColor Green
