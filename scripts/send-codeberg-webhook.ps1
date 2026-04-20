#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ScriptName {
    if ($PSCommandPath) {
        return Split-Path -Leaf $PSCommandPath
    }

    return 'send-codeberg-webhook.ps1'
}

function Show-Usage {
    $scriptName = Get-ScriptName

    @"
Usage: $scriptName -u URL -s SECRET -r REPO_ID -i PR_NUMBER [options]

Required:
  -u URL        Listener URL (e.g. http://localhost:8080/webhooks/v1/providers/forgejo/<pathKey>)
  -s SECRET     Forgejo/Codeberg webhook secret (must match the ProPR webhook secret)
    -r REPO_ID    Repository id used by Forgejo APIs. When omitted as a numeric id and provided
                                as owner/repo, the value is reused as a best-effort fallback external id.
  -i PR_NUMBER  Pull request number (integer)

Options:
  -O OWNER      Repository owner/namespace (default: acme)
    -P PATH       Repository path_with_namespace (for example: local_admin/propr).
                                Recommended whenever -r is a numeric repository id.
  -a ACTION     Pull request action; can be provided multiple times.
                Defaults: opened, review_requested
                Add closed explicitly when you want to test lifecycle cancellation.
  -S SOURCE     Source branch (default: feature/test-branch)
  -T TARGET     Target branch (default: main)
  -R REVIEWER   Requested reviewer login for review_requested events (default: meister-review-bot)
  -M            Mark closed events as merged (default: false)
  -n N          Repeat each action N times (default: 1)
  -h            Show this help

Example:
    pwsh scripts/send-codeberg-webhook.ps1 -u http://localhost:8080/webhooks/v1/providers/forgejo/ddfccfe1645e40c4a8e61c4516a11a74 `
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD -r 101 -P acme/propr -i 24

Path-only fallback example:
    pwsh scripts/send-codeberg-webhook.ps1 -u http://localhost:8080/webhooks/v1/providers/forgejo/ddfccfe1645e40c4a8e61c4516a11a74 `
    -s 95F0E081F5524B81E287179AFDF31E0F6B03408EED1601A3643209A0AAD3E1BD -r local_admin/propr -i 24
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

    if ($Value.Contains('/webhooks/v1/codeberg/') -and -not $Value.Contains('/webhooks/v1/providers/')) {
        return $Value.Replace('/webhooks/v1/codeberg/', '/webhooks/v1/providers/forgejo/')
    }

    if ($Value.Contains('/webhooks/v1/forgejo/') -and -not $Value.Contains('/webhooks/v1/providers/')) {
        return $Value.Replace('/webhooks/v1/forgejo/', '/webhooks/v1/providers/forgejo/')
    }

    return $Value
}

function Compute-Signature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Secret,

        [Parameter(Mandatory = $true)]
        [string]$Payload
    )

    $hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($Secret))
    try {
        $hash = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Payload))
        return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $hmac.Dispose()
    }
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

$repoId = $null
$owner = 'acme'
$projectPath = $null
$repoName = $null
$prNumber = $null
$sourceBranch = 'feature/test-branch'
$targetBranch = 'main'
$reviewerLogin = 'meister-review-bot'
$markMerged = $false
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
        '-r' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -r' }
            $repoId = $args[++$index]
        }
        '-i' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -i' }
            $prNumber = $args[++$index]
        }
        '-O' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -O' }
            $owner = $args[++$index]
        }
        '-P' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -P' }
            $projectPath = $args[++$index]
        }
        '-a' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -a' }
            $actions.Add($args[++$index])
        }
        '-S' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -S' }
            $sourceBranch = $args[++$index]
        }
        '-T' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -T' }
            $targetBranch = $args[++$index]
        }
        '-R' {
            if ($index + 1 -ge $args.Count) { Write-UsageError 'Missing argument for -R' }
            $reviewerLogin = $args[++$index]
        }
        '-M' {
            $markMerged = $true
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
    [string]::IsNullOrWhiteSpace($prNumber)) {
    [Console]::Error.WriteLine('Missing required argument.')
    Show-Usage
    exit 2
}

$url = Normalize-ListenerUrl -Value $url

if ($actions.Count -eq 0) {
    foreach ($defaultAction in @('opened', 'review_requested')) {
        $actions.Add($defaultAction)
    }
}

$prNumberValue = Convert-ToPositiveInteger -Label 'PR_NUMBER' -Value $prNumber
$repeatCount = Convert-ToPositiveInteger -Label 'REPEAT' -Value $repeat

function Resolve-RepositoryIdentity {
    if (-not [string]::IsNullOrWhiteSpace($projectPath)) {
        $script:projectPath = $projectPath.Trim('/').Trim()
    }
    elseif ($repoId.Contains('/')) {
        $script:projectPath = $repoId.Trim('/').Trim()
    }
    else {
        $script:projectPath = "$owner/$repoId"
    }

    if (-not $script:projectPath.Contains('/')) {
        Write-UsageError 'Repository path must be an owner/repository path. Use -P owner/repo when -r is a numeric repository id.'
    }

    $segments = $script:projectPath.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Length -lt 2) {
        Write-UsageError 'Repository path must include both owner and repository name.'
    }

    $script:owner = [string]::Join('/', $segments[0..($segments.Length - 2)])
    $script:repoName = $segments[$segments.Length - 1]
}

Resolve-RepositoryIdentity

function Build-Payload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ActionName
    )

    $mergedValue = if ($markMerged -and $ActionName -eq 'closed') { 'true' } else { 'false' }
    $requestedReviewer = if ($ActionName -eq 'review_requested') {
        ',"requested_reviewer":{"id":99,"login":"' + (Escape-JsonValue $reviewerLogin) + '","type":"Bot"}'
    } else {
        ''
    }

    $state = if ($ActionName -eq 'closed') { 'closed' } else { 'open' }
    $repoIdJson = if ($repoId -match '^[0-9]+$') { $repoId } else { '"' + (Escape-JsonValue $repoId) + '"' }

    return @"
{"action":"$(Escape-JsonValue $ActionName)","repository":{"id":$repoIdJson,"full_name":"$(Escape-JsonValue $projectPath)","owner":{"login":"$(Escape-JsonValue $owner)"},"name":"$(Escape-JsonValue $repoName)"},"pull_request":{"id":$((100000 + $prNumberValue)),"number":$prNumberValue,"state":"$state","merged":$mergedValue,"head":{"ref":"$(Escape-JsonValue $sourceBranch)","sha":"$(Escape-JsonValue "$ActionName-head-sha")"},"base":{"ref":"$(Escape-JsonValue $targetBranch)","sha":"base-sha"}}$requestedReviewer,"sender":{"id":7,"login":"octocat","type":"User"}}
"@
}

try {
    foreach ($actionName in $actions) {
        for ($iteration = 0; $iteration -lt $repeatCount; $iteration++) {
            $deliveryId = Get-DeliveryId
            $payload = Build-Payload -ActionName $actionName
            $signature = Compute-Signature -Secret $secret -Payload $payload

            Write-Host ("Sending Forgejo action '{0}' -> {1}" -f $actionName, $url)

            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
            try {
                $request.Headers.TryAddWithoutValidation('X-Gitea-Signature', $signature) | Out-Null
                $request.Headers.TryAddWithoutValidation('X-Gitea-Event', 'pull_request') | Out-Null
                $request.Headers.TryAddWithoutValidation('X-Gitea-Delivery', $deliveryId) | Out-Null
                $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')

                $httpClient = [System.Net.Http.HttpClient]::new()
                try {
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
                    $httpClient.Dispose()
                }
            }
            finally {
                $request.Dispose()
            }

            Write-Host
        }
    }
}
finally {
}

exit 0
