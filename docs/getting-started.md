# Getting Started — Meister ProPR Backend

Meister ProPR is an ASP.NET Core 10 backend that accepts Azure DevOps pull request review
requests, fetches the changed files using a backend-controlled Azure identity, runs an AI review
via the **Azure OpenAI Responses API** (reasoning + tool use), and posts the findings back as
PR thread comments.

Each API client can supply its own Azure service principal credentials so that all ADO operations
run under that client's identity — or fall back to the global backend identity.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Quick Start (Docker)](#quick-start-docker)
- [Running Locally (dotnet)](#running-locally-dotnet)
- [Azure Setup](#azure-setup)
- [Environment Variables](#environment-variables)
- [Per-Client ADO Credentials](#per-client-ado-credentials)
- [Client Management API](#client-management-api)
- [API Usage](#api-usage)
- [Observability](#observability)
- [Running the Tests](#running-the-tests)

---

## Prerequisites

| Requirement                                       | Version             |
|---------------------------------------------------|---------------------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.103 or later   |
| Azure subscription                                | —                   |
| Azure OpenAI **or** Azure AI Foundry resource     | —                   |
| Azure DevOps organisation                         | —                   |
| PostgreSQL 17 (or Docker)                         | 17.x                |
| Docker (optional, for container runs)             | any recent version  |

---

## Quick Start (Docker)

The fastest way to run Meister ProPR locally is `docker compose`, which starts the API and a
PostgreSQL 17 container together.

### Step 1 — Create a `.env` file

Create `.env` at the repo root (next to `docker-compose.yml`):

```env
# --- Admin & client keys ---
MEISTER_ADMIN_KEY=your-admin-key-here
MEISTER_CLIENT_KEYS=your-client-key-here

# --- AI ---
AI_ENDPOINT=https://myresource.openai.azure.com/
AI_DEPLOYMENT=gpt-4o
# AI_API_KEY=           # omit to use DefaultAzureCredential

# --- Global Azure identity (DefaultAzureCredential env-var chain) ---
# Used for ADO operations when a client has no per-client credentials configured.
AZURE_CLIENT_ID=<global-service-principal-appId>
AZURE_TENANT_ID=<azure-tenant-id>
AZURE_CLIENT_SECRET=<global-service-principal-secret>

# --- Database ---
# The docker-compose.yml wires this automatically; override only if using an external DB.
# DB_CONNECTION_STRING=Host=...

# --- PR crawler ---
# PR_CRAWL_INTERVAL_SECONDS=60

# --- Observability (optional) ---
# OTLP_ENDPOINT=http://localhost:4317
```

> Keep this file out of source control. It is already listed in `.gitignore`.

### Step 2 — Start the stack

```bash
docker compose up --build
```

The API is available on `https://localhost:5443` (HTTPS) and `http://localhost:8080` (HTTP).
The container runs as a non-root user and performs its own health check every 30 seconds.

### Step 3 — Register your first client

```bash
# Replace your-admin-key-here with the value from your .env file
curl -k -X POST https://localhost:5443/clients \
  -H "Content-Type: application/json" \
  -H "X-Admin-Key: your-admin-key-here" \
  -d '{"key": "my-secret-client-key", "displayName": "My First Client"}'
```

Note the returned `id` (a UUID) — you will need it in subsequent steps.

### Step 4 — (Optional) Set per-client ADO credentials

If you want a client to authenticate against Azure DevOps using its **own** service principal
rather than the global backend identity, store the credentials:

```bash
# <client-id> is the UUID from Step 3
curl -k -X PUT https://localhost:5443/clients/<client-id>/ado-credentials \
  -H "Content-Type: application/json" \
  -H "X-Admin-Key: your-admin-key-here" \
  -d '{
    "tenantId": "<azure-tenant-id>",
    "clientId": "<service-principal-appId>",
    "secret":   "<service-principal-secret>"
  }'
```

The secret is stored in the database and never returned in API responses.

To remove per-client credentials and revert to the global identity:

```bash
curl -k -X DELETE https://localhost:5443/clients/<client-id>/ado-credentials \
  -H "X-Admin-Key: your-admin-key-here"
```

### Step 5 — Add a crawl configuration

Tell the backend which Azure DevOps project to monitor for PRs assigned to a specific reviewer:

```bash
curl -k -X POST https://localhost:5443/clients/<client-id>/crawl-configurations \
  -H "Content-Type: application/json" \
  -H "X-Client-Key: my-secret-client-key" \
  -d '{
    "organizationUrl": "https://dev.azure.com/my-org",
    "projectId": "my-project-name-or-guid",
    "reviewerDisplayName": "My Service Principal",
    "crawlIntervalSeconds": 60
  }'
```

`reviewerDisplayName` must match the display name of the Azure DevOps identity (user or service
principal) whose assigned PRs you want reviewed.

The backend will begin polling for open PRs assigned to that reviewer and submit reviews
automatically.

---

## Running Locally (dotnet)

```bash
# Clone and enter the repo
git clone <repo-url>
cd meister-propr

# Set required config via user secrets
dotnet user-secrets set "AI_ENDPOINT"         "https://myresource.openai.azure.com/"  --project src/MeisterProPR.Api
dotnet user-secrets set "AI_DEPLOYMENT"       "gpt-4o"                                --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_ADMIN_KEY"   "my-admin-key"                          --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_CLIENT_KEYS" "my-secret-key"                         --project src/MeisterProPR.Api
dotnet user-secrets set "DB_CONNECTION_STRING" "Host=localhost;Database=meisterpropr;Username=postgres;Password=devpassword" --project src/MeisterProPR.Api

# Global service principal for DefaultAzureCredential (if not using Azure CLI / VS auth)
dotnet user-secrets set "AZURE_CLIENT_ID"     "<appId>"    --project src/MeisterProPR.Api
dotnet user-secrets set "AZURE_TENANT_ID"     "<tenant>"   --project src/MeisterProPR.Api
dotnet user-secrets set "AZURE_CLIENT_SECRET" "<password>" --project src/MeisterProPR.Api

# Run
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/MeisterProPR.Api
```

The API starts on `https://localhost:5443` (HTTPS) and `http://localhost:5080` (HTTP).

Verify it is healthy:

```bash
curl -k https://localhost:5443/healthz
```

Expected response:

```json
{"status":"Healthy","results":{"worker":{"status":"Healthy","description":"Worker is running."}}}
```

In `Development` mode, Swagger UI is available at `https://localhost:5443/swagger`.

---

## Azure Setup

### 1 — AI endpoint

The backend uses the **Azure OpenAI Responses API** and accepts endpoints from two sources:

**Option A — Azure OpenAI**

1. Create an **Azure OpenAI** resource in the Azure portal.
2. Deploy a model that supports the Responses API (e.g. `gpt-4o`, `o4-mini`) and note the
   **deployment name**.
3. The endpoint looks like `https://<resource-name>.openai.azure.com/`.

**Option B — Azure AI Foundry**

1. Open your AI Foundry project in the portal.
2. Copy the **project endpoint** shown on the overview page, e.g.
   `https://<resource>.services.ai.azure.com/api/projects/<project>`.
3. Use the **model name** (e.g. `gpt-4o`) as `AI_DEPLOYMENT`.

---

### 2 — Create an Azure service principal (Entra ID app registration)

The backend needs an Azure identity to authenticate against Azure DevOps. You can create a
dedicated service principal (recommended) or reuse an existing one.

**In the Azure portal (Entra ID → App registrations):**

1. Click **New registration**.
2. Give it a name, e.g. `meister-propr-backend`.
3. Leave **Redirect URI** blank and click **Register**.
4. Note the **Application (client) ID** and **Directory (tenant) ID** from the overview page.
5. Go to **Certificates & secrets → Client secrets → New client secret**.
6. Give it a description, choose an expiry, and click **Add**.
7. **Copy the secret value immediately** — it is only shown once.
8. Add the principal to the organization in Azure DevOps.

---

### 3 — Grant the service principal access in Azure DevOps

The service principal must be a member of each Azure DevOps project it will review PRs for.

1. In Azure DevOps, go to **Project settings → Permissions** (or **Teams**) for your project.
2. Click **Add** and search for the service principal name or its client ID.
3. Assign it at least the **Contributor** role so it can read PRs and post comment threads.

Repeat for every project whose PRs the backend will review.

> If you use **per-client credentials** (see [Per-client ADO credentials](#per-client-ado-credentials)
> below), each client's service principal must be granted access independently.

---

### 4 — Create a PostgreSQL database

The backend stores clients, crawl configurations, and review jobs in PostgreSQL.

**Docker (quickest for local dev):**

```bash
docker run -d \
  --name meister-propr-pg \
  -e POSTGRES_PASSWORD=devpassword \
  -e POSTGRES_DB=meisterpropr \
  -p 5432:5432 \
  postgres:17-alpine
```

Connection string: `Host=localhost;Database=meisterpropr;Username=postgres;Password=devpassword`

EF Core runs migrations automatically on startup — no manual schema setup is needed.

---

## Environment Variables

### Required

| Variable              | Description                                                                   |
|-----------------------|-------------------------------------------------------------------------------|
| `AI_ENDPOINT`         | Azure OpenAI endpoint (`https://….openai.azure.com/`) **or** Azure AI Foundry project URL (`https://….services.ai.azure.com/api/projects/…`) |
| `AI_DEPLOYMENT`       | Model deployment name, e.g. `gpt-4o` or `o4-mini`                            |
| `MEISTER_ADMIN_KEY`   | Admin API key sent in the `X-Admin-Key` header when managing clients          |
| `MEISTER_CLIENT_KEYS` | Comma-separated client keys — required when `DB_CONNECTION_STRING` is not set; acts as bootstrap seed when it is set |

The application **will not start** if required variables are missing or empty.

### Optional

| Variable                    | Description                                                                         |
|-----------------------------|-------------------------------------------------------------------------------------|
| `DB_CONNECTION_STRING`      | PostgreSQL connection string. When set, enables DB mode (persisted jobs + client registry). |
| `PR_CRAWL_INTERVAL_SECONDS` | Polling interval in seconds for the PR crawler background worker (default `60`, minimum `10`). |
| `AI_API_KEY`                | API key for the AI endpoint. Omit to use `DefaultAzureCredential`.                 |
| `AZURE_CLIENT_ID`           | Global service principal app ID (`DefaultAzureCredential` env-var chain)           |
| `AZURE_TENANT_ID`           | Azure AD tenant ID                                                                  |
| `AZURE_CLIENT_SECRET`       | Global service principal secret (local dev — **never commit**)                     |
| `CORS_ORIGINS`              | Extra comma-separated allowed CORS origins                                          |
| `OTLP_ENDPOINT`             | OTLP collector URL for traces, e.g. `http://localhost:4317`                        |
| `ASPNETCORE_ENVIRONMENT`    | `Development` enables Swagger UI; defaults to `Production`                         |

**Built-in CORS origins** (always allowed): `http://localhost:3000`, `https://localhost:3000`,
`https://dev.azure.com`, `*.visualstudio.com`.

### Development-only bypasses

> [!WARNING]
> Set these via `dotnet user-secrets` only. **Never set them in production.**

| Variable                    | Effect                                                                              |
|-----------------------------|-------------------------------------------------------------------------------------|
| `ADO_SKIP_TOKEN_VALIDATION` | `true` — accept any non-empty `X-Ado-Token` without calling the VSS endpoint       |
| `ADO_STUB_PR`               | `true` — use a fake PR and skip ADO comment posting; real AI endpoint still called  |

---

## Per-Client ADO Credentials

By default all ADO operations use the **global backend identity** configured via
`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET` (or a managed identity in
production). This works well when all clients belong to the same Azure DevOps organisation and
the same service principal has been granted access.

When clients belong to **different organisations or tenants**, or when you want isolation between
clients, each client can have its own Azure service principal:

1. **Create a service principal** in Entra ID for each client (see
   [Azure Setup — step 2](#2--create-an-azure-service-principal-entra-id-app-registration)).
2. **Grant it access** to the relevant Azure DevOps projects (see
   [Azure Setup — step 3](#3--grant-the-service-principal-access-in-azure-devops)).
3. **Store the credentials** via `PUT /clients/{id}/ado-credentials` (see
   [Step 4](#step-4--optional-set-per-client-ado-credentials) above).

The `GET /clients` and `GET /clients/{id}` responses include `hasAdoCredentials`,
`adoTenantId`, and `adoClientId` — the secret is never returned.

**How the backend resolves the credential for each ADO call:**

```mermaid
flowchart TD
    START([ADO Operation\nfor Client X]) --> LOOKUP

    LOOKUP{Per-client credentials\nin DB?}

    LOOKUP -- "yes" --> CSC["ClientSecretCredential\n(tenantId, clientId, secret)"]
    LOOKUP -- "no" --> DAC["DefaultAzureCredential\n(env vars / managed identity)"]

    CSC --> CACHE_C["VssConnection\n(client-scoped cache)"]
    DAC --> CACHE_G["VssConnection\n(global cache)"]

    CACHE_C --> ADO[Azure DevOps API]
    CACHE_G --> ADO
```

---

## Client Management API

All client management endpoints require the `X-Admin-Key` header.

| Method   | Path                                         | Description                                 |
|----------|----------------------------------------------|---------------------------------------------|
| `POST`   | `/clients`                                   | Register a new client                       |
| `GET`    | `/clients`                                   | List all clients                            |
| `GET`    | `/clients/{id}`                              | Get a single client                         |
| `PATCH`  | `/clients/{id}`                              | Enable or disable a client                  |
| `DELETE` | `/clients/{id}`                              | Delete a client                             |
| `PUT`    | `/clients/{id}/ado-credentials`              | Set (or replace) per-client ADO credentials |
| `DELETE` | `/clients/{id}/ado-credentials`              | Clear per-client ADO credentials            |
| `POST`   | `/clients/{id}/crawl-configurations`         | Add a crawl configuration                   |
| `GET`    | `/clients/{id}/crawl-configurations`         | List crawl configurations for a client      |
| `DELETE` | `/clients/{id}/crawl-configurations/{cfgId}` | Remove a crawl configuration                |

---

## API Usage

Review endpoints require both `X-Client-Key` (from `MEISTER_CLIENT_KEYS` or the client
registry) and `X-Ado-Token` (a valid ADO PAT or OAuth token used only for caller identity
verification).

### Submit a review

```bash
# organizationUrl, projectId, repositoryId, pullRequestId, iterationId are all required
curl -k -X POST https://localhost:5443/reviews \
  -H "X-Client-Key: my-secret-client-key" \
  -H "X-Ado-Token: <your-ado-pat>" \
  -H "Content-Type: application/json" \
  -d '{
    "organizationUrl": "https://dev.azure.com/my-org",
    "projectId": "my-project",
    "repositoryId": "my-repo",
    "pullRequestId": 42,
    "iterationId": 1
  }'
```

Response `202 Accepted`:

```json
{ "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

Submitting the same PR and iteration while a job is still active returns the **same** job ID
(idempotent).

### Poll for the result

```bash
# Replace the UUID with the jobId from the 202 response
curl -k https://localhost:5443/reviews/3fa85f64-5717-4562-b3fc-2c963f66afa6 \
  -H "X-Client-Key: my-secret-client-key" \
  -H "X-Ado-Token: <your-ado-pat>"
```

While processing, `status` is `"pending"` or `"processing"`. When done:

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "completed",
  "submittedAt": "2026-03-09T10:00:00Z",
  "completedAt": "2026-03-09T10:00:45Z",
  "result": {
    "summary": "Overall the PR looks good. One potential issue found.",
    "comments": [
      {
        "filePath": "src/MyService.cs",
        "lineNumber": 42,
        "severity": "warning",
        "message": "Consider extracting this logic into a separate method."
      }
    ]
  }
}
```

---

## Observability

| Signal             | How to access                                                            |
|--------------------|--------------------------------------------------------------------------|
| Structured logs    | Written to stdout (JSON in non-Development environments)                 |
| Health check       | `GET /healthz` — reports worker liveness                                 |
| Prometheus metrics | `GET /metrics` — job counters and timing                                 |
| OTLP traces        | Set `OTLP_ENDPOINT` to point at a collector (e.g. Jaeger, Grafana Alloy) |

---

## Running the Tests

```bash
dotnet test
```

235 tests across four projects should pass without additional setup. The API integration tests
use `WebApplicationFactory` with fake credentials and in-memory stubs. Infrastructure
integration tests spin up a real PostgreSQL 17 container automatically via Testcontainers
(Docker or Podman required).
