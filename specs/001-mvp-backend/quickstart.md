# Quickstart: MVP Backend — Local Development

**Branch**: `001-mvp-backend` | **Date**: 2026-03-03

## Prerequisites

| Requirement               | Version | Notes                                                |
|---------------------------|---------|------------------------------------------------------|
| .NET SDK                  | 10.0    | `dotnet --version` to verify                         |
| Docker (optional)         | 27+     | For `docker compose up` stack                        |
| Azure subscription        | —       | For AI endpoint and ADO access                       |
| Azure DevOps organisation | —       | Service principal must have read/comment permissions |

---

## Required Environment Variables

Set these before running the backend. All are required unless marked optional.

| Variable              | Example                                | Description                                                                                  |
|-----------------------|----------------------------------------|----------------------------------------------------------------------------------------------|
| `MEISTER_CLIENT_KEYS` | `my-secret-key-1,my-secret-key-2`      | Comma-separated valid client keys. The `X-Client-Key` header is validated against this list. |
| `AI_ENDPOINT`         | `https://my-hub.openai.azure.com`      | Azure OpenAI endpoint URL (as provided by Azure AI Foundry).                                 |
| `AI_DEPLOYMENT`       | `gpt-4o`                               | Model deployment name.                                                                       |
| `AZURE_CLIENT_ID`     | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | Service principal client ID (local dev).                                                     |
| `AZURE_TENANT_ID`     | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | Azure tenant ID (local dev).                                                                 |
| `AZURE_CLIENT_SECRET` | `your-secret-value`                    | Service principal secret (local dev). **Do not commit.**                                     |

In production (Azure container), `AZURE_CLIENT_ID/TENANT_ID/CLIENT_SECRET` are
replaced by managed identity — no code changes required. `DefaultAzureCredential`
resolves the correct credential type automatically.

### Setting Variables (local shell)

**Windows (PowerShell)**:

```powershell
$env:MEISTER_CLIENT_KEYS = "dev-key-123"
$env:AI_ENDPOINT         = "https://my-hub.openai.azure.com"
$env:AI_DEPLOYMENT       = "gpt-4o"
$env:AZURE_CLIENT_ID     = "..."
$env:AZURE_TENANT_ID     = "..."
$env:AZURE_CLIENT_SECRET = "..."
```

**Linux / macOS / bash**:

```bash
export MEISTER_CLIENT_KEYS="dev-key-123"
export AI_ENDPOINT="https://my-hub.openai.azure.com"
export AI_DEPLOYMENT="gpt-4o"
export AZURE_CLIENT_ID="..."
export AZURE_TENANT_ID="..."
export AZURE_CLIENT_SECRET="..."
```

Or use a `.env` file at the repository root (never commit it — it is in `.gitignore`).

---

## Run Locally

```bash
dotnet run --project src/MeisterProPR.Api
```

Hot-reload during development:

```bash
dotnet watch --project src/MeisterProPR.Api
```

The service listens on `http://localhost:5000` by default.

---

## Verify the Service is Running

```bash
curl http://localhost:5000/healthz
```

Expected response: `200 OK`

```json
{ "status": "Healthy", "worker": "running" }
```

---

## Submit a Review

```bash
curl -s -X POST http://localhost:5000/reviews \
  -H "Content-Type: application/json" \
  -H "X-Client-Key: dev-key-123" \
  -H "X-Ado-Token: <your-ado-pat-or-oauth-token>" \
  -d '{
    "organizationUrl": "https://dev.azure.com/myorg",
    "projectId": "my-project",
    "repositoryId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "pullRequestId": 42,
    "iterationId": 1
  }'
```

Expected response: `202 Accepted`

```json
{ "jobId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" }
```

---

## Poll for Results

```bash
curl -s http://localhost:5000/reviews/{jobId} \
  -H "X-Client-Key: dev-key-123" \
  -H "X-Ado-Token: <your-ado-token>"
```

Poll until `"status"` is `"completed"` or `"failed"`. Expected completion
response:

```json
{
  "jobId": "...",
  "status": "completed",
  "submittedAt": "2026-03-03T10:00:00Z",
  "completedAt": "2026-03-03T10:01:30Z",
  "result": {
    "summary": "Overall review narrative...",
    "comments": [
      { "filePath": "/src/Foo.cs", "lineNumber": 42,
        "severity": "warning", "message": "Consider null check here." }
    ]
  }
}
```

---

## Run Tests

```bash
dotnet test
```

All tests must pass before opening a PR. Tests use xUnit + NSubstitute;
integration tests use `WebApplicationFactory<Program>` — no real network calls.

---

## Run with Docker Compose

```bash
docker compose up
```

Requires the same env vars set in `docker-compose.yml` or a `.env` file at the
repository root. The production-like image uses:

- Runtime: `mcr.microsoft.com/dotnet/aspnet:10.0` (Linux rootless)
- Build: `mcr.microsoft.com/dotnet/sdk:10.0`

---

## Identity Shown on ADO Comments

When the backend posts PR review comments it uses `DefaultAzureCredential`,
which resolves to the service principal (local dev) or managed identity
(production). The name shown on the comment thread in ADO is the **Azure AD
display name** of that credential:

| Environment | Credential        | Name shown on PR comments                         |
|-------------|-------------------|---------------------------------------------------|
| Local dev   | Service principal | Display name of the Azure AD **app registration** |
| Production  | Managed identity  | Display name of the **managed identity** resource |

To ensure comments appear as *"Meister ProPR"* (or any desired bot name), set
that display name on the Azure AD app registration used for local development.
Using a suffix like *"Meister ProPR (Dev)"* is fine — it keeps local and
production identities distinguishable during testing.

The service principal (or managed identity) must be added to the ADO
organisation as a user and granted at minimum **Reader** access plus
**Contribute to pull requests** on the target repository before comment
posting will succeed.

---

## Common Errors

| Error                                         | Cause                                            | Fix                                                                                     |
|-----------------------------------------------|--------------------------------------------------|-----------------------------------------------------------------------------------------|
| `AI_ENDPOINT environment variable is not set` | Missing env var on startup                       | Set `AI_ENDPOINT`                                                                       |
| `401 Unauthorized` on POST /reviews           | Invalid `X-Client-Key`                           | Ensure header matches a value in `MEISTER_CLIENT_KEYS`                                  |
| Job fails with "ADO auth error"               | Service principal lacks ADO permissions          | Grant the service principal `Reader` + `Contribute to pull requests` on the target repo |
| Job fails with "AI endpoint error"            | AI endpoint unreachable or deployment name wrong | Verify `AI_ENDPOINT` and `AI_DEPLOYMENT` env vars                                       |
