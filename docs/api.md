# API Reference - ProPR Backend

This page contains technical API examples for automating administrative tasks that are
also available from the Admin UI. Use the Admin UI for interactive configuration; use the
endpoints below for automation and scripting.

> NOTE: This file intentionally contains low-level `curl` examples. For UI-guided steps,
> use the Admin UI (https://localhost:5443/) after first login.

## Admin Authentication

Exchange admin credentials for a JWT (used by subsequent admin calls):

```bash
curl -k -X POST https://localhost:5443/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "admin", "password": "<strong-password-here>"}'
```

Response contains `accessToken` and `refreshToken`. Use `Authorization: Bearer <token>` on
subsequent admin requests. To refresh the access token, call `POST /api/auth/refresh` with
the refresh token.

## Client Management

Create a client:

```bash
curl -k -X POST https://localhost:5443/api/clients \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"displayName": "My First Client"}'
```

Set per-client Azure DevOps credentials (service principal):

```bash
# <client-id> is the UUID returned when creating the client
curl -k -X PUT https://localhost:5443/api/clients/<client-id>/ado-credentials \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "tenantId": "<azure-tenant-id>",
    "clientId": "<service-principal-appId>",
    "secret":   "<service-principal-secret>"
  }'
```

Remove per-client credentials (revert to global identity):

```bash
curl -k -X DELETE https://localhost:5443/api/clients/<client-id>/ado-credentials \
  -H "Authorization: Bearer <accessToken>"
```

Resolve and store a reviewer identity (VSS identity GUID):

```bash
curl -k "https://localhost:5443/api/identities/resolve?orgUrl=https://dev.azure.com/my-org&displayName=My%20Service%20Principal" \
  -H "Authorization: Bearer <accessToken>"

# Store the chosen identity on the client
curl -k -X PUT https://localhost:5443/api/clients/<client-id>/reviewer-identity \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"reviewerId": "<resolved-vss-identity-guid>"}'
```

Register an allowed Azure DevOps organization for the client:

```bash
curl -k -X POST https://localhost:5443/api/clients/<client-id>/ado-organization-scopes \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <accessToken>" \
  -d '{
    "organizationUrl": "https://dev.azure.com/my-org",
    "displayName": "My Org"
  }'
```

The returned scope `id` is used by discovery, guided source creation, and crawl configuration.

## Guided Discovery Endpoints

These endpoints are used by the Admin UI to populate cascading dropdowns.

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

Create a guided ProCursor source (repository example):

```bash
curl -k -X POST https://localhost:5443/api/admin/clients/<client-id>/procursor/sources \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{
    "displayName": "Platform Docs",
    "sourceKind": "repository",
    "organizationScopeId": "<scope-id>",
    "projectId": "my-project",
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
    "projectId": "my-project",
    "crawlIntervalSeconds": 60,
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

## Observability

Health check endpoint:

```bash
curl -k https://localhost:5443/healthz
```

In Development mode, Swagger UI is available at `https://localhost:5443/swagger`.

## More

For additional endpoints (prompt overrides, dismissal search, token reporting, and ProCursor token usage),
search the source code or consult the API OpenAPI specification in `openapi.json` at the repo root.
