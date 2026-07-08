# API Reference - ProPR Backend

This page contains technical API examples for automating administrative tasks that are also
available from the frontend. Use the frontend for interactive configuration; use the endpoints
below for automation and scripting.

> NOTE: This file intentionally contains low-level `curl` examples. For UI-guided steps, use the
> frontend (`https://localhost:5443/`) after first login.
>
> The `curl -k` examples below are for local development against the self-signed
> `https://localhost:5443` endpoint only.

## Admin Authentication

Exchange admin credentials for a JWT:

```bash
curl -k -X POST https://localhost:5443/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "admin", "password": "<strong-password-here>"}'
```

Response contains `accessToken` and `refreshToken`. Use `Authorization: Bearer <token>` on
subsequent admin requests. To refresh the access token, call `POST /api/auth/refresh` with the
refresh token.

## Client Management

Create a client:

```bash
curl -k -X POST https://localhost:5443/api/clients \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"displayName": "My First Client", "tenantId": "<tenant-id>", "defaultReviewStrategy": "fileByFile"}'
```

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

## AI Connection Profiles

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
        "capabilities": ["chat"]
      }
    ],
    "purposeBindings": [
      {
        "purpose": "reviewDefault",
        "remoteModelId": "gpt-5.4-mini"
      },
      {
        "purpose": "proRvPrefilter",
        "remoteModelId": "gpt-5.4-mini"
      }
    ]
  }'
```

The optional `proRvPrefilter` binding gives the ProRV applicability screen a dedicated runtime in the
offline evaluation harness's ProRV-prefilter mode. In production, a `prorv` review-pass lens runs its
applicability screen on the pass entry's own configured model, so this binding is not required there.

## Guided Discovery Endpoints

These endpoints are used by the frontend to populate Azure DevOps guided discovery flows.

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

## ProCursor Source Management

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

## Crawl Configurations

Resolve repository filters then create a crawl configuration:

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

When `selectedSources` is used the chosen source IDs are snapshotted onto queued review jobs.
`reviewTemperature` is optional. Omit it to keep the model's default temperature behavior.

## Webhook Configurations

Webhook configurations are managed per client and can coexist with crawl configurations for the same
repositories. The create response returns the listener URL and a one-time secret that must be copied
into the provider-side webhook registration.

For a user-focused walkthrough and configuration checklist, see [docs/webhooks.md](webhooks.md).

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

Webhook configurations also accept optional `reviewTemperature` values between `0.0` and `2.0`.

Expected create response highlights:

- `listenerUrl`: public HTTPS path under `/webhooks/v1/providers/{provider}/{pathKey}`
- `generatedSecret`: returned once at creation time
- `repoFilters`: server-owned persisted repository and branch scope

Inspect recent delivery history for one webhook configuration:

```bash
curl -k "https://localhost:5443/api/admin/webhook-configurations/<config-id>/deliveries?take=20" \
  -H "Authorization: Bearer <accessToken>"
```

Each delivery-history entry records the sanitized incoming event summary, final outcome, response
status, and the downstream actions that were invoked. Outcomes are:

- `accepted`: the delivery triggered normal downstream routing
- `ignored`: the delivery was valid but intentionally treated as a no-op
- `rejected`: auth, path, scope, or payload validation failed
- `failed`: validation succeeded but downstream activation or lifecycle sync failed after intake

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

## Public Webhook Receiver

Azure DevOps webhooks should target the one-time `listenerUrl` returned by webhook configuration
creation and use Basic auth with the generated secret as the password.

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

The webhook receiver returns a compact acknowledgement payload when delivery validation succeeds,
even if the event is intentionally ignored:

```json
{ "status": "accepted" }
```

```json
{ "status": "ignored" }
```

After acknowledgement, the delivery history for the matching configuration records the sanitized
event summary, final outcome, and shared synchronization actions. Webhook-triggered and
crawler-triggered PR activity converge on the same pull-request synchronization path before any
review job is queued or cancelled.

The receiver acknowledges authenticated deliveries quickly and returns a small status payload.
Typical values are `accepted`, `ignored`, or `failed`. Repository and branch scope are always
enforced from the saved webhook configuration, not from caller intent.

## Review Diagnostics

Execution trace investigation stays inside the existing review-local job protocol experience.

- `GET /api/jobs/{id}/protocol` returns the review's protocol passes and event metadata needed by the execution traces tab, including review-local trace filtering fields such as `eventCategory`. The optional `includeEvents` query controls whether the overview omits bulky event bodies while preserving visible trace rows.
- Each protocol event now includes `eventCategory` so the frontend can derive review-local filters and suggestions without calling a separate diagnostics endpoint.
- Operators investigate one opened review in place; there is no standalone client-wide trace investigation API in this slice.

## Observability

Health check endpoint:

```bash
curl -k https://localhost:5443/healthz
```

In Development mode, Swagger UI is available at `https://localhost:5443/swagger`.

## More

For additional endpoints such as prompt overrides, dismissal search, token reporting, ProCursor token
usage, tenant administration, and review diagnostics, consult the OpenAPI specification in
`openapi.json` at the repo root.
