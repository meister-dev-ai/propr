# Getting Started — Meister ProPR Backend

Meister ProPR is an ASP.NET Core 10 backend that accepts Azure DevOps pull request review
requests, fetches the changed files using the backend's own Azure identity, runs an AI review
via the **Azure OpenAI Responses API** (reasoning + tool use), and posts the findings back as
PR thread comments.

---

## Prerequisites

| Requirement                                       | Version            |
|---------------------------------------------------|--------------------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.103 or later  |
| Azure subscription                                | —                  |
| Azure OpenAI **or** Azure AI Foundry resource     | —                  |
| Azure DevOps organisation                         | —                  |
| Docker (optional, for container runs)             | any recent version |

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
   The backend strips the project path automatically — only the resource root is used for
   inference.
3. Use the **model name** (e.g. `gpt-4o`) as `AI_DEPLOYMENT`.

**Authentication**

By default the backend uses `DefaultAzureCredential` (managed identity / service principal) —
no API key is needed. Assign the identity the **Cognitive Services OpenAI User** role on the
resource.

If you prefer API key auth, set `AI_API_KEY`.

### 2 — Azure DevOps permissions

The backend fetches PR content and posts comments using its own Azure identity — the
`X-Ado-Token` sent by callers is used only to verify the caller's identity and is **never**
forwarded to Azure DevOps.

Grant the identity that will run the backend at least **Contributor** access to the Azure
DevOps projects whose PRs it will review.

The required ADO resource scope is `499b84ac-1321-427f-aa17-267ca6975798/.default`
(the Azure DevOps resource ID).

### 3 — Identity for local development

Locally, `DefaultAzureCredential` resolves credentials in this order (among others):
environment variables → Azure CLI → Visual Studio. The easiest approach is a **service
principal**:

```bash
az ad sp create-for-rbac --name meister-propr-local --skip-assignment
```

Then assign the service principal the roles described in steps 1 and 2.

For production deployments, use a **managed identity** — no credential environment variables
are needed.

---

## Environment Variables

### Required

| Variable              | Description                                                                  |
|-----------------------|------------------------------------------------------------------------------|
| `AI_ENDPOINT`         | Azure OpenAI endpoint (`https://…openai.azure.com/`) **or** Azure AI Foundry project URL (`https://….services.ai.azure.com/api/projects/…`) |
| `AI_DEPLOYMENT`       | Model deployment name, e.g. `gpt-4o` or `o4-mini`                           |
| `MEISTER_CLIENT_KEYS` | Comma-separated API keys for callers, e.g. `key1,key2`                      |

The application **will not start** if any required variable is missing or empty.

### Optional

| Variable                    | Description                                                                         |
|-----------------------------|-------------------------------------------------------------------------------------|
| `AI_API_KEY`                | API key for the AI endpoint. Omit to use `DefaultAzureCredential`.                 |
| `AZURE_CLIENT_ID`           | Service principal app ID (local dev)                                               |
| `AZURE_TENANT_ID`           | Azure AD tenant ID (local dev)                                                     |
| `AZURE_CLIENT_SECRET`       | Service principal secret (local dev — **never commit**)                            |
| `CORS_ORIGINS`              | Extra comma-separated allowed CORS origins beyond the built-in defaults             |
| `OTLP_ENDPOINT`             | OTLP collector URL for traces, e.g. `http://localhost:4317`                        |
| `ASPNETCORE_ENVIRONMENT`    | `Development` enables Swagger UI; defaults to `Production`                         |

**Built-in CORS origins** (always allowed): `http://localhost:3000`, `https://localhost:3000`,
`https://dev.azure.com`, `*.visualstudio.com`.

### Development-only bypasses

Set these via `dotnet user-secrets` (see below). **Never set them in production.**

| Variable                    | Effect                                                                              |
|-----------------------------|-------------------------------------------------------------------------------------|
| `ADO_SKIP_TOKEN_VALIDATION` | `true` — accept any non-empty `X-Ado-Token` without calling the VSS endpoint       |
| `ADO_STUB_PR`               | `true` — use a fake PR and skip ADO comment posting; real AI endpoint still called  |

---

## Running Locally

The recommended approach for local development is **user secrets** so sensitive values never
touch environment variables or source control:

```bash
# Clone and enter the repo
git clone <repo-url>
cd meister-propr

# Set required config via user secrets
dotnet user-secrets set "AI_ENDPOINT"         "https://myresource.openai.azure.com/"  --project src/MeisterProPR.Api
dotnet user-secrets set "AI_DEPLOYMENT"       "gpt-4o"                                --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_CLIENT_KEYS" "my-secret-key"                         --project src/MeisterProPR.Api

# Service principal for DefaultAzureCredential (if not using Azure CLI / VS auth)
dotnet user-secrets set "AZURE_CLIENT_ID"     "<appId>"    --project src/MeisterProPR.Api
dotnet user-secrets set "AZURE_TENANT_ID"     "<tenant>"   --project src/MeisterProPR.Api
dotnet user-secrets set "AZURE_CLIENT_SECRET" "<password>" --project src/MeisterProPR.Api

# Optional: bypass ADO validation and PR fetching for testbed use
dotnet user-secrets set "ADO_SKIP_TOKEN_VALIDATION" "true" --project src/MeisterProPR.Api
dotnet user-secrets set "ADO_STUB_PR"               "true" --project src/MeisterProPR.Api

# Run
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/MeisterProPR.Api
```

The API starts on `http://localhost:5000` (or the port shown in the console).

Verify it is healthy:

```bash
curl http://localhost:5000/healthz
```

Expected response when the background worker has started:

```json
{"status":"Healthy","results":{"worker":{"status":"Healthy","description":"Worker is running."}}}
```

In `Development` mode, Swagger UI is available at `http://localhost:5000/swagger`.

---

## Running with Docker

A `docker-compose.yml` is provided at the repo root. Create a `.env` file alongside it:

```env
MEISTER_CLIENT_KEYS=my-secret-key
AI_ENDPOINT=https://myresource.openai.azure.com/
AI_DEPLOYMENT=gpt-4o
# AI_API_KEY=          # omit to use DefaultAzureCredential
AZURE_CLIENT_ID=<appId>
AZURE_TENANT_ID=<tenant>
AZURE_CLIENT_SECRET=<password>
```

Then start the service:

```bash
docker compose up --build
```

The API is available on `http://localhost:8080`. The container runs as a non-root user and
performs its own health check every 30 seconds.

To build and run the image directly:

```bash
docker build -t meister-propr .
docker run -p 8080:8080 \
  -e AI_ENDPOINT="https://myresource.openai.azure.com/" \
  -e AI_DEPLOYMENT="gpt-4o" \
  -e MEISTER_CLIENT_KEYS="my-secret-key" \
  -e AZURE_CLIENT_ID="..." \
  -e AZURE_TENANT_ID="..." \
  -e AZURE_CLIENT_SECRET="..." \
  meister-propr
```

---

## API Usage

All requests require an `X-Client-Key` header matching one of the values in
`MEISTER_CLIENT_KEYS`. Review endpoints also require an `X-Ado-Token` header containing a
valid Azure DevOps personal access token (PAT) or OAuth token — this token is used only to
verify the caller's identity and is **not** used to fetch PR data.

### Submit a review

```bash
curl -X POST http://localhost:5000/reviews \
  -H "X-Client-Key: my-secret-key" \
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
curl http://localhost:5000/reviews/3fa85f64-5717-4562-b3fc-2c963f66afa6 \
  -H "X-Client-Key: my-secret-key" \
  -H "X-Ado-Token: <your-ado-pat>"
```

While processing, `status` is `"pending"` or `"processing"`. When done:

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "completed",
  "submittedAt": "2026-03-03T10:00:00Z",
  "completedAt": "2026-03-03T10:00:45Z",
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

If the job failed, `status` is `"failed"` and an `error` string is included.

### List reviews

```bash
curl http://localhost:5000/reviews \
  -H "X-Client-Key: my-secret-key" \
  -H "X-Ado-Token: <your-ado-pat>"
```

Returns all jobs submitted by this client key, newest first.

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

All 141 tests across four projects should pass without any additional setup — the API
integration tests use `WebApplicationFactory` with fake credentials and in-memory stubs.

---

## Architecture at a Glance

```
POST /reviews
     │
     ▼
ClientKeyMiddleware  ←── validates X-Client-Key
     │
     ▼
ReviewsController    ←── validates X-Ado-Token (identity check only)
     │  returns 202 + jobId immediately
     ▼
InMemoryJobRepository  (ConcurrentDictionary, lost on restart)
     │
     ▼ (every 2 seconds)
ReviewJobWorker (BackgroundService)
     │
     ▼
ReviewOrchestrationService
     ├── AdoPullRequestFetcher  ──► Azure DevOps           (DefaultAzureCredential)
     ├── AgentAiReviewCore      ──► Azure OpenAI / Foundry (Responses API)
     └── AdoCommentPoster       ──► Azure DevOps           (DefaultAzureCredential)
```

**State is in-memory.** Restarting the process loses all pending and completed jobs.
A future persistence layer can be added by implementing `IJobRepository`.
