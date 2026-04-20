#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# send-forgejo-webhook.ps1
# Thin alias for the shared Forgejo/Codeberg webhook helper.

$scriptDir = Split-Path -Parent $PSCommandPath
$targetScript = Join-Path $scriptDir 'send-codeberg-webhook.ps1'

if (-not (Test-Path -LiteralPath $targetScript)) {
    [Console]::Error.WriteLine("Missing helper script: $targetScript")
    exit 1
}

$forwardedArgs = [System.Collections.Generic.List[string]]::new()
if ($args.Count -gt 0 -and $args[0] -eq '--help') {
    $forwardedArgs.Add('-h')
}
else {
    foreach ($argument in $args) {
        $forwardedArgs.Add($argument)
    }
}

if ($forwardedArgs.Count -gt 0 -and $forwardedArgs[0] -eq '-h') {
    $scriptName = Split-Path -Leaf $PSCommandPath
    @"
Usage: $scriptName -u URL -s SECRET -r REPO_ID -i PR_NUMBER [options]

This Forgejo-named wrapper delegates to $(Split-Path -Leaf $targetScript).
Forgejo and Codeberg use the same webhook payload and signature format in ProPR,
so all options are forwarded to the shared implementation.

"@ | Write-Output
}

& $targetScript @forwardedArgs
exit $LASTEXITCODE
