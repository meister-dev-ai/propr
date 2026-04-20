#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ScriptName {
    if ($PSCommandPath) {
        return Split-Path -Leaf $PSCommandPath
    }

    return 'send-gitlab-webhook.ps1'
}

function Show-Usage {
    $scriptName = Get-ScriptName

    @"
Usage: $scriptName -u URL -s SECRET -p PROJECT_ID -i MR_IID [options]

Required:
  -u URL        Listener URL (e.g. http://localhost:8080/webhooks/v1/providers/gitlab/<pathKey>)
  -s SECRET     GitLab secret token (must match the ProPR webhook secret)
  -p PROJECT_ID  GitLab project id (numeric or string)
  -i MR_IID     Merge request iid (integer)

Options:
  -P PATH       project path_with_namespace (default: PROJECT_ID)
  -a ACTION     Merge request action; can be provided multiple times.
                Defaults: open, update
                Add merge explicitly when you want to test lifecycle cancellation.
  -S SOURCE     Source branch (default: feature/test-branch)
  -T TARGET     Target branch (default: main)
  -U USERNAME   Merge request author username (default: propr-local)
  -D NAME       Merge request author display name (default: ProPR Local)
  -n N          Repeat each action N times (default: 1)
  -h            Show this help

Example:
    pwsh scripts/send-gitlab-webhook.ps1 -u http://localhost:8080/webhooks/v1/providers/gitlab/ddfccfe1645e40c4a8e61c4516a11a74 `
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD -p 101 -P acme/platform/propr -i 24
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

    if ($Value.Contains('/webhooks/v1/gitlab/') -and -not $Value.Contains('/webhooks/v1/providers/')) {
        return $Value.Replace('/webhooks/v1/gitlab/', '/webhooks/v1/providers/gitlab/')
    }

    if ($Value.Contains('/webhooks/v1/providers/gitLab/')) {
        return $Value.Replace('/webhooks/v1/providers/gitLab/', '/webhooks/v1/providers/gitlab/')
    }

    return $Value
}

function Get-DeliveryId {
    return [guid]::NewGuid().ToString()
}

function Escape-JsonValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return ($Value -replace '\\', '\\\\' -replace '"', '\\"' -replace "`n", '\\n' -replace "`r", '\\r' -replace "`t", '\\t')
}

$projectId = $null
$projectPath = $null
$mrIid = $null
$sourceBranch = 'feature/test-branch'
$targetBranch = 'main'
$authorUsername = 'propr-local'
$authorName = 'ProPR Local'
$repeat = '1'
$actions = [System.Collections.Generic.List[string]]::new()

$url = $null
$secret = $null

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
        '-p' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -p' }
            $projectId = $args[++$index]
        }
        '-i' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -i' }
            $mrIid = $args[++$index]
        }
        '-P' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -P' }
            $projectPath = $args[++$index]
        }
        '-S' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -S' }
            $sourceBranch = $args[++$index]
        }
        '-T' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -T' }
            $targetBranch = $args[++$index]
        }
        '-a' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -a' }
            $actions.Add($args[++$index])
        }
        '-U' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -U' }
            $authorUsername = $args[++$index]
        }
        '-D' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -D' }
            $authorName = $args[++$index]
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
    [string]::IsNullOrWhiteSpace($projectId) -or
    [string]::IsNullOrWhiteSpace($mrIid)) {
    [Console]::Error.WriteLine('Missing required argument.')
    Show-Usage
    exit 2
}

$url = Normalize-ListenerUrl -Value $url

if ($actions.Count -eq 0) {
    foreach ($defaultAction in @('open', 'update')) {
        $actions.Add($defaultAction)
    }
}

if ([string]::IsNullOrWhiteSpace($projectPath)) {
    $projectPath = $projectId
}

$mrIidNumber = Convert-ToPositiveInteger -Label 'MR_IID' -Value $mrIid
$repeatCount = Convert-ToPositiveInteger -Label 'REPEAT' -Value $repeat

$projectName = $projectPath.Split('/')[-1]
if ([string]::IsNullOrWhiteSpace($projectName)) {
    $projectName = $projectPath
}

$namespacePath = $projectName
if ($projectPath.Contains('/')) {
    $namespacePath = $projectPath.Substring(0, $projectPath.LastIndexOf('/'))
}

$httpClient = [System.Net.Http.HttpClient]::new()

function Send-Action {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ActionName
    )

    $deliveryId = Get-DeliveryId
    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $projectIdJson = if ($projectId -match '^[0-9]+$') { $projectId } else { '"' + (Escape-JsonValue $projectId) + '"' }
    $payload = @"
{"object_kind":"merge_request","event_type":"merge_request","user":{"id":42,"name":"$(Escape-JsonValue $authorName)","username":"$(Escape-JsonValue $authorUsername)"},"project":{"id":$projectIdJson,"name":"$(Escape-JsonValue $projectName)","path_with_namespace":"$(Escape-JsonValue $projectPath)","namespace":{"id":1,"name":"$(Escape-JsonValue $namespacePath)","path":"$(Escape-JsonValue $namespacePath)","kind":"group","full_path":"$(Escape-JsonValue $namespacePath)"}},"object_attributes":{"id":$((100000 + $mrIidNumber)),"iid":$mrIidNumber,"action":"$(Escape-JsonValue $ActionName)","source_branch":"$(Escape-JsonValue $sourceBranch)","target_branch":"$(Escape-JsonValue $targetBranch)","last_commit":{"id":"$(Escape-JsonValue "$ActionName-head-sha")"},"created_at":"$timestamp","updated_at":"$timestamp"}}
"@

    Write-Host ("Sending merge request action '{0}' -> {1}" -f $ActionName, $url)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
    try {
        $request.Headers.TryAddWithoutValidation('X-Gitlab-Token', $secret) | Out-Null
        $request.Headers.TryAddWithoutValidation('X-Gitlab-Event', 'Merge Request Hook') | Out-Null
        $request.Headers.TryAddWithoutValidation('X-Gitlab-Event-UUID', $deliveryId) | Out-Null
        $request.Headers.TryAddWithoutValidation('Idempotency-Key', $deliveryId) | Out-Null
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
    foreach ($actionName in $actions) {
        for ($iteration = 0; $iteration -lt $repeatCount; $iteration++) {
            Send-Action -ActionName $actionName
            Write-Host
        }
    }
}
finally {
    $httpClient.Dispose()
}

exit 0
