#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ScriptName {
    if ($PSCommandPath) {
        return Split-Path -Leaf $PSCommandPath
    }

    return 'send-ado-webhook.ps1'
}

function Show-Usage {
    $scriptName = Get-ScriptName

    @"
Usage: $scriptName -u URL -s SECRET -r REPO_ID -i PR_ID [options]

Required:
    -u URL        Listener URL (e.g. http://localhost:8080/webhooks/v1/providers/ado/<pathKey>)
  -s SECRET     Webhook secret (shared secret stored in ProPR)
  -r REPO_ID    Repository id or name (string)
  -i PR_ID      Pull request id (integer)

Options:
  -S SOURCE     Source ref (default: refs/heads/feature/test-branch)
  -T TARGET     Target ref (default: refs/heads/main)
  -E EVENT      Event type; can be provided multiple times.
                Defaults: git.pullrequest.created, git.pullrequest.updated, git.pullrequest.commented
  -U USERNAME   Basic auth username (default: propr)
  -k STATUS     Pull request status (default: active)
  -n N          Repeat each event N times (default: 1)
  -h            Show this help

Example:
    pwsh scripts/send-ado-webhook.ps1 -u http://localhost:8080/webhooks/v1/providers/ado/ddfccfe1645e40c4a8e61c4516a11a74 `
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD -r meister-propr -i 24
"@
}

function Write-UsageError {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [Console]::Error.WriteLine($Message)
    Show-Usage
    exit 2
}

function Convert-ToPositiveInteger {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $parsedValue = 0
    if (-not [int]::TryParse($Value, [ref]$parsedValue) -or $parsedValue -lt 1) {
        [Console]::Error.WriteLine("$Label must be a positive integer.")
        exit 2
    }

    return $parsedValue
}

function Normalize-ListenerUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value.Contains('/webhooks/v1/ado/') -and -not $Value.Contains('/webhooks/v1/providers/')) {
        return $Value.Replace('/webhooks/v1/ado/', '/webhooks/v1/providers/ado/')
    }

    return $Value
}

$source = 'refs/heads/feature/test-branch'
$target = 'refs/heads/main'
$username = 'propr'
$prStatus = 'active'
$repeat = '1'
$events = [System.Collections.Generic.List[string]]::new()

$url = $null
$secret = $null
$repoId = $null
$prId = $null

for ($index = 0; $index -lt $args.Count; $index++) {
    $argument = $args[$index]

    switch -CaseSensitive ($argument) {
        '-u' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -u' }
            $url = $args[++$index]
        }
        '-s' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -s' }
            $secret = $args[++$index]
        }
        '-r' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -r' }
            $repoId = $args[++$index]
        }
        '-i' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -i' }
            $prId = $args[++$index]
        }
        '-S' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -S' }
            $source = $args[++$index]
        }
        '-T' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -T' }
            $target = $args[++$index]
        }
        '-E' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -E' }
            $events.Add($args[++$index])
        }
        '-U' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -U' }
            $username = $args[++$index]
        }
        '-k' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -k' }
            $prStatus = $args[++$index]
        }
        '-n' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -n' }
            $repeat = $args[++$index]
        }
        '-h' {
            Show-Usage
            exit 0
        }
        default {
            if ($argument.StartsWith('-')) {
                Write-UsageError "Invalid option: $argument"
            }

            Write-UsageError "Unexpected argument: $argument"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($url) -or
    [string]::IsNullOrWhiteSpace($secret) -or
    [string]::IsNullOrWhiteSpace($repoId) -or
    [string]::IsNullOrWhiteSpace($prId)) {
    [Console]::Error.WriteLine('Missing required argument.')
    Show-Usage
    exit 2
}

$url = Normalize-ListenerUrl -Value $url

if ($events.Count -eq 0) {
    foreach ($defaultEvent in @('git.pullrequest.created', 'git.pullrequest.updated', 'git.pullrequest.commented')) {
        $events.Add($defaultEvent)
    }
}

$prIdNumber = Convert-ToPositiveInteger -Label 'PR_ID' -Value $prId
$repeatCount = Convert-ToPositiveInteger -Label 'REPEAT' -Value $repeat
$authBytes = [System.Text.Encoding]::UTF8.GetBytes("$username`:$secret")
$authB64 = [Convert]::ToBase64String($authBytes)
$httpClient = [System.Net.Http.HttpClient]::new()

function Send-Event {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EventName
    )

    $payload = @{
        eventType = $EventName
        resource = @{
            repository = @{
                id = $repoId
            }
            pullRequestId = $prIdNumber
            sourceRefName = $source
            targetRefName = $target
            status = $prStatus
            reviewers = @()
        }
    } | ConvertTo-Json -Depth 6 -Compress

    Write-Host ("Sending event '{0}' -> {1}" -f $EventName, $url)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
    try {
        $request.Headers.TryAddWithoutValidation('Authorization', "Basic $authB64") | Out-Null
        $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')

        $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not [string]::IsNullOrEmpty($body)) {
                Write-Output $body
            }

            Write-Output "HTTP_STATUS:$([int]$response.StatusCode)"
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

try {
    foreach ($eventName in $events) {
        for ($iteration = 0; $iteration -lt $repeatCount; $iteration++) {
            Send-Event -EventName $eventName
            Write-Host
        }
    }
}
finally {
    $httpClient.Dispose()
}

exit 0