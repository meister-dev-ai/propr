# API reference - ProPR backend

The endpoints below automate administrative tasks the frontend also offers. Use the frontend for
interactive configuration and these endpoints for scripting.

Every example targets the evaluation stack's origin, `https://localhost:5443`. Its certificate is
self-signed, so the examples pass `curl -k`. Behind your own ingress, substitute your API base URL.

## Admin authentication

Exchange admin credentials for a JWT:

```bash
curl -k -X POST https://localhost:5443/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "admin", "password": "<strong-password-here>"}'
```

The response body carries `accessToken`, `expiresIn` (seconds) and `tokenType`. The refresh token is
issued as an httpOnly cookie, not in the body. Send `Authorization: Bearer <accessToken>` on subsequent
requests. `POST /api/auth/refresh` reads that cookie and returns a new access token. It accepts a
`refreshToken` field in the request body as a fallback for callers that cannot hold cookies.

`GET /api/auth/me` returns the caller's global role, per-client and per-tenant roles, the installation
edition, and the state of every licensed capability.

### Personal access tokens

For scripts and CI, use a personal access token, not an admin password. Create one under
**Settings → Personal Access Tokens** in the frontend, or:

```bash
curl -k -X POST https://localhost:5443/api/users/me/pats \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"label": "ci-pipeline", "expiresAt": "2027-01-01T00:00:00Z"}'
```

`label` is required; `expiresAt` is optional. The plaintext `token` is returned once and cannot be
retrieved again. Send it in place of the bearer header:

```bash
curl -k https://localhost:5443/api/clients \
  -H "X-User-Pat: <token>"
```

List tokens with `GET /api/users/me/pats` and revoke one with `DELETE /api/users/me/pats/<pat-id>`. What
a PAT can do, and how it is stored, is in [automation credentials](security.md#automation-credentials).

## Client management

Create a client:

```bash
curl -k -X POST https://localhost:5443/api/clients \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"displayName": "My First Client", "tenantId": "<tenant-id>"}'
```

`tenantId` is required. List the tenants you may create a client in with `GET /api/admin/tenants`. A fresh
installation seeds exactly one, the built-in **System** tenant, whose id is always
`11111111-1111-1111-1111-111111111111`. Creating a client anywhere else needs the tenant-administrator role for
that tenant; only a platform administrator can create one in the System tenant.

List provider connections for a client:

```bash
curl -k https://localhost:5443/api/clients/<client-id>/provider-connections \
  -H "Authorization: Bearer <accessToken>"
```

Create an Azure DevOps provider connection:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/provider-connections \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "providerFamily": "azureDevOps",
    "hostBaseUrl": "https://dev.azure.com",
    "authenticationKind": "oauthClientCredentials",
    "oAuthTenantId": "<tenant-id>",
    "oAuthClientId": "<application-client-id>",
    "displayName": "Contoso Azure DevOps",
    "secret": "<client-secret-value>",
    "isActive": true
  }'
```

Create a GitHub provider connection:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/provider-connections \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "providerFamily": "github",
    "hostBaseUrl": "https://github.com",
    "authenticationKind": "personalAccessToken",
    "displayName": "GitHub Cloud",
    "secret": "<github-pat>",
    "isActive": true
  }'
```

Create a GitHub App provider connection, where ProPR acts as an installed App:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/provider-connections \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "providerFamily": "github",
    "hostBaseUrl": "https://github.com",
    "authenticationKind": "appInstallation",
    "gitHubAppId": 123456,
    "gitHubAppInstallationId": 789012,
    "displayName": "GitHub App",
    "secret": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----",
    "isActive": true
  }'
```

`providerFamily` and `authenticationKind` are required, and which other fields each combination needs
is in [the support matrix](../platforms/index.md#support-matrix).

Patch one provider connection:

```bash
curl -k -X PATCH https://localhost:5443/api/clients/<client-id>/provider-connections/<connection-id> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "displayName": "Primary GitHub",
    "isActive": true
  }'
```

Create a provider scope on a connection:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/provider-connections/<connection-id>/scopes \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "scopeType": "organization",
    "externalScopeId": "my-org",
    "scopePath": "https://dev.azure.com/my-org",
    "displayName": "My Org",
    "isEnabled": true
  }'
```

Verify a provider connection:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/provider-connections/<connection-id>/verify \
  -H "Authorization: Bearer <accessToken>"
```

Resolve and store a reviewer identity for a provider connection:

```bash
curl -k "https://localhost:5443/api/clients/<client-id>/provider-connections/<connection-id>/reviewer-identities/resolve?search=My%20Service%20Principal" \
  -H "Authorization: Bearer <accessToken>"

curl -k -X PUT https://localhost:5443/api/clients/<client-id>/provider-connections/<connection-id>/reviewer-identity \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "externalUserId": "<resolved-provider-user-id>",
    "login": "my-service-principal",
    "displayName": "My Service Principal",
    "isBot": true
  }'
```

## AI connection profiles

Create an AI connection profile:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/ai-connections \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "displayName": "Foundry Primary",
    "providerKind": "azureOpenAi",
    "baseUrl": "https://my-foundry.services.ai.azure.com/models",
    "auth": {
      "mode": "apiKey",
      "apiKey": "<api-key>"
    },
    "discoveryMode": "manualOnly",
    "configuredModels": [
      {
        "remoteModelId": "gpt-5.4-mini",
        "displayName": "gpt-5.4-mini",
        "operationKinds": ["chat"]
      }
    ],
    "purposeBindings": [
      {
        "purpose": "reviewDefault",
        "remoteModelId": "gpt-5.4-mini"
      }
    ]
  }'
```

`providerKind` is required, `displayName` is required and capped at 200 characters, and
`configuredModels` and `purposeBindings` are both required. An absent or empty array on either is
rejected with `400`.

### Purpose bindings

A binding names a `purpose` and the model on this profile that serves it. Name the model by `remoteModelId`,
matched against `configuredModels` in the same request, or by `configuredModelId` once the model has an id.
`protocolMode` defaults to `auto` and `isEnabled` to `true`. A purpose may appear once.

`purpose` takes the API value of any purpose listed under [AI purposes](../ai/purposes.md#ai-purposes).
`embeddingDefault` needs an embedding-capable model and a `protocolMode` of `auto` or `embeddings`; every other
purpose needs a chat-capable model.

Logical models select a model first, and these bindings resolve after them. See
[Logical models and purposes](#logical-models-and-purposes) below.

### Per-model inputs

Each entry in `configuredModels` takes these fields.

| Field | Notes |
|---|---|
| `remoteModelId` | Required; unique within the request, case-insensitively |
| `displayName` | Defaults to `remoteModelId` |
| `operationKinds` | `chat`, `embedding`, or both |
| `supportedProtocolModes` | `auto`, `responses`, `chatCompletions`, `embeddings`, `anthropicMessages`, `bedrockConverse`, `googleGenerateContent` |
| `tokenizerName`, `maxInputTokens`, `embeddingDimensions` | Mandatory for an embedding model |
| `supportsStructuredOutput`, `supportsToolUse` | Default `false` |
| `maxContextTokens` | Context window, used for context budgeting |
| `inputCostPer1MUsd`, `outputCostPer1MUsd`, `cachedInputCostPer1MUsd` | Prices used for spend reporting |
| `id`, `source`, `lastSeenAt` | Round-tripped from discovery; omit on a hand-written model |

The server infers `operationKinds` when you omit it: `embedding` when the model id contains `embedding` or when
`tokenizerName` or `embeddingDimensions` is supplied, `chat` otherwise. It infers `supportedProtocolModes` the
same way: `auto` and `embeddings` for an embedding-only model, `auto`, `responses` and `chatCompletions`
otherwise. A declared protocol the model's capabilities do not support is rejected: the embeddings protocol on a
model without embedding capability, or a chat protocol on a model without chat capability.

An embedding model must carry `tokenizerName`, a `maxInputTokens` above zero, and an `embeddingDimensions` between
64 and 4096. `source` is `discovered`, `manual` or `knownCatalog`.

`providerKind` accepts `azureOpenAi`, `openAi`, `liteLlm`, `openAiCompatible`, `anthropic`, `awsBedrock`,
and `googleVertex`. Ask the API which of those this build can call, and which the tenant permits:

```bash
curl -k https://localhost:5443/api/clients/<client-id>/ai-connections/permitted-providers \
  -H "Authorization: Bearer <accessToken>"
```

Probe a target before saving it, and discover the models behind it:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/ai-connections/probe \
  -H "Content-Type: application/json" -H "Authorization: Bearer <accessToken>" \
  -d '{ "providerKind": "anthropic", "baseUrl": "https://api.anthropic.com/v1",
        "auth": { "mode": "apiKey", "apiKey": "<api-key>" } }'

curl -k -X POST https://localhost:5443/api/clients/<client-id>/ai-connections/discover-models \
  -H "Content-Type: application/json" -H "Authorization: Bearer <accessToken>" \
  -d '{ "providerKind": "anthropic", "baseUrl": "https://api.anthropic.com/v1",
        "auth": { "mode": "apiKey", "apiKey": "<api-key>" } }'
```

### Logical models and purposes

Which model serves which workload is configured through logical models, not on the profile. A logical model is a
name you choose that points at one model on one connection; a purpose is a fixed slot the review loop asks for.

| Call | Does |
|---|---|
| `GET /api/clients/<client-id>/logical-models` | The names effective for this client - its own overrides plus the tenant-catalog entries they do not shadow |
| `POST /api/clients/<client-id>/logical-models/overrides` | Defines a per-client name |
| `PUT /api/clients/<client-id>/logical-models/purposes/<purpose>` | Points one purpose at a name |
| `GET /api/tenants/<tenant-id>/logical-models` | The tenant-catalog names every client in that tenant inherits |

Tenant-catalog writes are refused for the System tenant, so a client in it defines its names as client overrides.

The model catalog and its tenant pricing overrides live under `/api/tenants/<tenant-id>/model-catalog/`
(`models`, `providers`, `overrides`). See [models and the catalog](../ai/models-and-catalog.md).

### Scripted setup

Create, verify, activate, bind, in that order. Activation is refused while the profile has not been verified
since its last change.

```bash
BASE=https://localhost:5443/api/clients/<client-id>
AUTH=(-H "Authorization: Bearer <accessToken>" -H "Content-Type: application/json")

# 1. Create. The response carries the profile id and an id for each configured model.
profile=$(curl -sk -X POST "$BASE/ai-connections" "${AUTH[@]}" -d '{
  "displayName": "Bedrock Frankfurt",
  "providerKind": "awsBedrock",
  "baseUrl": "https://bedrock-runtime.eu-central-1.amazonaws.com",
  "auth": { "mode": "apiKey", "apiKey": "<accessKeyId>:<secretAccessKey>" },
  "discoveryMode": "manualOnly",
  "defaultQueryParams": { "region": "eu-central-1" },
  "configuredModels": [
    {
      "remoteModelId": "<bedrock-model-or-inference-profile-id>",
      "operationKinds": ["chat"],
      "supportedProtocolModes": ["auto", "bedrockConverse"],
      "maxContextTokens": 200000
    }
  ],
  "purposeBindings": [
    { "purpose": "reviewDefault", "remoteModelId": "<bedrock-model-or-inference-profile-id>" }
  ]
}')
connection_id=$(printf '%s' "$profile" | jq -r '.id')
model_id=$(printf '%s' "$profile" | jq -r '.configuredModels[0].id')

# 2. Verify. Stores a verification snapshot on the profile.
curl -sk -X POST "$BASE/ai-connections/$connection_id/verify" "${AUTH[@]}"

# 3. Activate. Returns 400 while the last verification did not succeed.
curl -sk -X POST "$BASE/ai-connections/$connection_id/activate" "${AUTH[@]}"

# 4. Name the model, then point a purpose at that name.
curl -sk -X POST "$BASE/logical-models/overrides" "${AUTH[@]}" -d "{
  \"name\": \"deep\",
  \"capability\": \"chat\",
  \"connectionId\": \"$connection_id\",
  \"configuredModelId\": \"$model_id\",
  \"reasoningEffort\": \"medium\",
  \"protocolMode\": \"auto\"
}"

curl -sk -X PUT "$BASE/logical-models/purposes/reviewDefault" "${AUTH[@]}" \
  -d '{"logicalModelName": "deep"}'
```

Repeat the last call for every purpose you use; an unmapped purpose fails the work that needs it.

`capability` is `chat` or `embedding`; `reasoningEffort` takes the values listed under
[reasoning effort](../ai/purposes.md#reasoning-effort).

`defaultQueryParams` carries the values a provider reads from the profile instead of the URL: `region` for AWS
Bedrock, `project` for Vertex AI. When to set each, and which wins if the host names one too, is in
[provider-specific setup notes](../ai/credentials.md#provider-specific-setup-notes). `defaultHeaders` sends extra
headers a gateway in front of a provider expects. Probe after setting one, because the families differ in where
headers are applied.

The credential is stored only when `auth.mode` is `apiKey`; Azure OpenAI also accepts `azureIdentity`, which uses
the host's managed identity and needs no key. What to send as the key for each family is in
[credentials by provider](../ai/credentials.md#credentials-by-provider).

## Guided discovery endpoints

Resolve the Azure DevOps projects, sources and branches reachable through a connection's organization
scope. All three need at least `ClientUser` for the client.

Add `&purpose=crawl` to the project and crawl-filter queries when you are building a crawl
configuration; that form is refused with HTTP 409 unless the `crawl-configs` capability is licensed.

```bash
# List projects
curl -k "https://localhost:5443/api/admin/clients/<client-id>/ado/discovery/projects?organizationScopeId=<scope-id>" \
  -H "Authorization: Bearer <accessToken>"

# List repository/wiki sources for a project
curl -k "https://localhost:5443/api/admin/clients/<client-id>/ado/discovery/sources?organizationScopeId=<scope-id>&projectId=my-project&sourceKind=repository" \
  -H "Authorization: Bearer <accessToken>"

# List branches for a repository
curl -k "https://localhost:5443/api/admin/clients/<client-id>/ado/discovery/branches?organizationScopeId=<scope-id>&projectId=my-project&sourceKind=repository&canonicalSourceProvider=azureDevOps&canonicalSourceValue=repo-1" \
  -H "Authorization: Bearer <accessToken>"
```

## ProCursor source management

Create a guided ProCursor source:

```bash
curl -k -X POST https://localhost:5443/api/admin/clients/<client-id>/procursor/sources \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{
    "displayName": "Platform Docs",
    "sourceKind": "repository",
    "organizationScopeId": "<scope-id>",
    "providerProjectKey": "my-project",
    "canonicalSourceRef": {
      "provider": "azureDevOps",
      "value": "repo-1"
    },
    "sourceDisplayName": "platform-docs",
    "defaultBranch": "main",
    "rootPath": "/docs",
    "symbolMode": "auto",
    "trackedBranches": [
      {
        "branchName": "main",
        "refreshTriggerMode": "branchUpdate",
        "miniIndexEnabled": true
      }
    ]
  }'
```

`sourceKind` is `repository` or `adoWiki`, and `refreshTriggerMode` is `manual` or `branchUpdate`.

## Crawl configurations

Crawl configurations require the `crawl-configs` capability; see [editions](editions.md). Resolve
repository filters, then create one:

```bash
curl -k "https://localhost:5443/api/admin/clients/<client-id>/ado/discovery/crawl-filters?organizationScopeId=<scope-id>&projectId=my-project" \
  -H "Authorization: Bearer <accessToken>"

curl -k -X POST https://localhost:5443/api/admin/crawl-configurations \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "clientId": "<client-id>",
    "organizationScopeId": "<scope-id>",
    "providerProjectKey": "my-project",
    "crawlIntervalSeconds": 60,
    "reviewTemperature": 0.2,
    "repoFilters": [
      {
        "displayName": "platform-docs",
        "canonicalSourceRef": {
          "provider": "azureDevOps",
          "value": "repo-1"
        },
        "targetBranchPatterns": ["main"]
      }
    ],
    "proCursorSourceScopeMode": "selectedSources",
    "proCursorSourceIds": ["<source-id>"]
  }'
```

`clientId` is required. When `selectedSources` is used the chosen source IDs are snapshotted onto
queued review jobs. `reviewTemperature` is optional; what it does and what it accepts is under
[what you can tune](../concepts/reviews.md#what-you-can-tune).

## Webhook configurations

Webhook configurations are managed per client and can coexist with crawl configurations for the same
repositories. [Webhooks](../platforms/webhooks.md) explains what one is, what to do with the secret it
returns, and how to read its deliveries.

```bash
# List webhook configurations visible to the caller
curl -k https://localhost:5443/api/admin/webhook-configurations \
  -H "Authorization: Bearer <accessToken>"

# Create a webhook configuration
curl -k -X POST https://localhost:5443/api/admin/webhook-configurations \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "clientId": "<client-id>",
    "provider": "azureDevOps",
    "organizationScopeId": "<scope-id>",
    "providerProjectKey": "my-project",
    "reviewTemperature": 0.15,
    "enabledEvents": [
      "pullRequestCreated",
      "pullRequestUpdated",
      "pullRequestCommented"
    ],
    "repoFilters": [
      {
        "repositoryName": "platform-docs",
        "displayName": "platform-docs",
        "canonicalSourceRef": {
          "provider": "azureDevOps",
          "value": "repo-1"
        },
        "targetBranchPatterns": ["main", "release/*"]
      }
    ]
  }'
```

`clientId` is required, `provider` is one of `azureDevOps`, `github`, `gitLab` or `forgejo`, and
`enabledEvents` accepts only the three names above. `reviewTemperature` is optional here too.

The create response carries:

- `listenerUrl`: the public HTTPS path your provider posts to
- `generatedSecret`: returned once at creation time
- `repoFilters`: the stored repository and branch scope, enforced on every delivery regardless of what
  the payload claims

Inspect recent delivery history for one webhook configuration:

```bash
curl -k "https://localhost:5443/api/admin/webhook-configurations/<config-id>/deliveries?take=20" \
  -H "Authorization: Bearer <accessToken>"
```

Each delivery-history entry records the sanitized incoming event summary, final outcome, response
status, and the downstream actions that were invoked. What each outcome and status means, and what to
change, is in [webhook troubleshooting](../platforms/webhooks.md#troubleshooting).

Update or delete an existing webhook configuration:

```bash
curl -k -X PATCH https://localhost:5443/api/admin/webhook-configurations/<config-id> \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "isActive": false,
    "reviewTemperature": 0.0,
    "enabledEvents": ["pullRequestUpdated"],
    "repoFilters": []
  }'

curl -k -X DELETE https://localhost:5443/api/admin/webhook-configurations/<config-id> \
  -H "Authorization: Bearer <accessToken>"
```

## Public webhook receiver

Point Azure DevOps webhooks at the one-time `listenerUrl` returned when the webhook configuration is
created, and use Basic auth with the generated secret as the password.

```bash
curl -k -X POST https://localhost:5443/webhooks/v1/providers/ado/<path-key> \
  -H "Authorization: Basic <base64(username:generated-secret)>" \
  -H "Content-Type: application/json" \
  -d '{
    "eventType": "git.pullrequest.updated",
    "resource": {
      "pullRequestId": 42,
      "repository": { "id": "repo-1" },
      "sourceRefName": "refs/heads/feature/webhooks",
      "targetRefName": "refs/heads/main",
      "status": "active"
    }
  }'
```

The receiver acknowledges every delivery that passes validation, including events it ignores:

```json
{ "status": "accepted" }
```

```json
{ "status": "ignored" }
```

The trigger decides when a review starts, not what it produces - see
[how a review gets triggered](../concepts/how-it-works.md#how-a-review-gets-triggered).

## Trigger a review

Submit a pull request for review. Requires the `ClientAdministrator` role for the client.

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/jobs \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "provider": "azureDevOps",
    "hostBaseUrl": "https://dev.azure.com",
    "repository": {
      "externalRepositoryId": "repo-1",
      "ownerOrNamespace": "my-org",
      "projectPath": "my-project"
    },
    "codeReview": {
      "platform": "pullRequest",
      "externalReviewId": "42",
      "number": 42
    },
    "reviewRevision": {
      "headSha": "<head-sha>",
      "baseSha": "<base-sha>",
      "providerRevisionId": "3"
    }
  }'
```

`provider`, `hostBaseUrl`, `repository` and `codeReview` are required; `number` must be at least 1 and
`platform` is `pullRequest` or `mergeRequest`. `reviewRevision` is optional, but if you send it,
`headSha` and `baseSha` are required within it.

Send `reviewRevision`. ProPR treats a submission as a duplicate of an in-flight job only when the whole
revision matches. Omit it and every submission for the same pull request collapses onto one revision, so
a second push cannot be reviewed while the first review is still running.

A successful submission returns `202 Accepted` with `jobId` and `status`. A `409 Conflict` means an
active job already exists for that revision, or the pull request is blocked. A review running on another
pull request, or on this one at an earlier revision, is not a conflict. The submission is queued and
starts once the installation's concurrency ceiling allows it - see [editions](editions.md).

### Trigger a review from coordinates alone

Post the coordinates when you know which pull request you mean but not which commits it is at. ProPR
reads the revision from your SCM host.

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/jobs/by-coordinates \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "providerScopePath": "https://dev.azure.com/my-org",
    "providerProjectKey": "my-project",
    "repositoryId": "<repository-id>",
    "pullRequestId": 42
  }'
```

All four fields are required, and `providerScopePath` and `providerProjectKey` must match a crawl or
webhook configuration of that client exactly. The match tells ProPR which provider family the
coordinates belong to, and it limits the client's source-control credential to the repositories that
client configured. Where the configuration lists specific repositories and recorded their provider ids,
`repositoryId` must be one of them; a configuration that lists none covers its whole scope. Deactivating
a configuration stops it starting reviews by itself and still lets you ask for one, which gives you a
manual-only setup. The review runs under that configuration's code-knowledge source scope and review
temperature.

The same request serves the first review and every re-review after new commits, because the revision is
read fresh each time. An earlier job at an older revision is retired as superseded. Unlike the automatic
triggers, this request reviews a revision that has already been reviewed, or that a previous review
failed at. A review already running at this exact revision is not started twice.

`ClientUser` is enough here, as it is for restart. Every answer to a complete request carries a named
`outcome`:

| `outcome` | Status | Means |
|---|---|---|
| `submitted` | 202 | A job was queued. `jobId` is the one to poll |
| `duplicateActiveJob` | 409 | A review of this revision is already running. `jobId` is that job |
| `notSubmittable` | 409 | The pull request is closed, merged, blocked, or its configured source scope no longer resolves. `reason` says which |
| `notAuthorized` | 403 | No configuration of this client covers the coordinates, or you lack the role |
| `pullRequestNotFound` | 404 | The provider reports no such pull request |
| `submissionFailed` | 500 | The pull request resolved, but queueing the review failed inside ProPR. The server logs carry the detail |
| `revisionUnresolvable` | 502 | The provider could not be asked, or answered without commits. Check the connection and retry |

A request missing one of the four fields carries no `outcome`. It is refused with `400` and a plain
`{"error": "..."}`, the shape the other endpoints on this page use.

Poll the job, and restart or stop it:

```bash
curl -k https://localhost:5443/api/reviewing/jobs/<job-id>/status \
  -H "X-User-Pat: <token>"

curl -k -X POST https://localhost:5443/api/reviewing/jobs/<job-id>/restart \
  -H "X-User-Pat: <token>"

curl -k -X POST https://localhost:5443/api/reviewing/jobs/<job-id>/stop \
  -H "X-User-Pat: <token>"
```

Reading status and restarting need only `ClientUser`; stopping needs `ClientAdministrator`. A failed
review is never continued automatically; restart it explicitly. Stopping is terminal and does not
requeue the job.

## Blocking and dismissing

Block a pull request so no further review jobs are created for it. This does not stop a job that is
already running - stop that job separately.

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/blocked-prs \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "providerScopePath": "https://dev.azure.com",
    "providerProjectKey": "my-project",
    "repositoryId": "repo-1",
    "pullRequestId": 42,
    "reason": "Generated code, not worth reviewing"
  }'
```

`GET` the same path to list current blocks, and `POST` the same body without `reason` to
`.../blocked-prs/unblock` to lift one. Listing needs `ClientUser`; blocking and unblocking need
`ClientAdministrator`.

Dismiss a finding so later reviews suppress similar ones:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/reviewing/dismiss-finding \
  -H "Content-Type: application/json" \
  -H "X-User-Pat: <token>" \
  -d '{
    "findingMessage": "Prefer a guard clause here",
    "filePath": "src/Program.cs",
    "label": "style-preference"
  }'
```

`findingMessage` is required; `filePath` and `label` are optional. The call returns `201` with the
stored memory record and requires `ClientAdministrator`.

## Review diagnostics

`GET /api/jobs/{id}/protocol` returns one review's protocol passes and events. Each event carries an
`eventCategory` you can filter on. Pass `includeEvents=false` for an overview that keeps the event
rows and metadata but omits their bodies, which is much cheaper on large reviews.

Diagnostics are scoped to a single review; there is no cross-review trace query.

Reading a protocol to answer a specific symptom starts at
[troubleshooting](../operate/troubleshooting.md).

## Usage statistics

Six platform-administrator endpoints cover the daily snapshot. [Usage statistics](usage-statistics.md) describes
each field of that snapshot and why it is collected.

`GET /api/admin/usage-statistics` returns the current state: whether sending is on, whether a commercial
license governs the control, the last send attempt and its outcome, and the newest version and advisories the
receiver last reported.

`PATCH /api/admin/usage-statistics` with `{"enabled": false}` switches sending off. It answers `409` while a
commercial license is installed.

`GET /api/admin/usage-statistics/preview` returns the request body the next snapshot would carry. Requesting it
sends nothing, in any state.

```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  https://localhost:5443/api/admin/usage-statistics/preview
```

`POST /api/admin/usage-statistics/send` runs a send cycle now instead of waiting for the daily one. The daily
rules still apply, so an installation that is switched off or has not shown the notice sends nothing. The
response carries a `decision` of `sent`, `disabled`, `awaitingConsent` or `notDue`, and the state after the
attempt.

```bash
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  https://localhost:5443/api/admin/usage-statistics/send
```

`POST /api/admin/usage-statistics/notice/shown` records that the consent notice reached an administrator, which
lets a community installation start sending. The administration UI calls it when the notice renders, and it is
idempotent. `POST /api/admin/usage-statistics/notice/dismiss` hides the notice and changes nothing about what is
sent.

## Health endpoints behind the proxy

Through the bundled reverse proxy, the API is reached under `/api/` - that prefix is stripped before
the request arrives, so the API's own `/healthz` is `/api/healthz` from outside:

```bash
curl -k https://localhost:5443/api/healthz
```

`https://localhost:5443/healthz` without the prefix is answered by the frontend container's own static
health string and reports nothing about the API. What each check reports, and which endpoint to point a
probe at, is in [observability](../operate/observability.md).

The same prefix rule applies to `/metrics`, which becomes `/api/metrics` - a path to
[block at your edge](security.md#what-to-block-at-your-edge).

## More

For every other endpoint - prompt overrides, dismissal search, token reporting, ProCursor token usage,
tenant administration - read the OpenAPI specification.

In Development the API serves it at its own address: Swagger UI at `/swagger`, the document at
`/swagger/v1/swagger.json`. Behind a reverse proxy that strips a leading `/api`, as the bundled one does, those
become `/api/swagger` and `/api/swagger/v1/swagger.json`. Other environments serve neither, and the bundled stack
runs in Production, so a stock deployment has no Swagger UI and no served document at any path.
`https://localhost:5443/swagger` without the prefix is answered by the frontend container.

Script against `openapi.json` at the repository root instead. It is committed, and it covers every endpoint on
this page except `/healthz`, `/livez` and `/metrics`, which [observability](../operate/observability.md)
describes. Its paths carry no `/api` prefix: `/clients/{clientId}/ai-connections`, not
`/api/clients/{clientId}/ai-connections`. Through the bundled proxy, prepend `https://localhost:5443/api` as
every example above does. The public webhook receiver is forwarded at its own path.
