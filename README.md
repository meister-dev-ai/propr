# Meister ProPR

An AI-powered code review backend for Azure DevOps pull requests.

When a PR is submitted for review, the backend fetches the changed files using its own Azure
identity, sends them to an Azure OpenAI model for analysis, and posts the findings back as
comment threads directly on the PR — one thread per finding, anchored to the relevant file and
line.

---

## How it works

```
POST /reviews  (from ADO extension or CI)
      │
      ▼
ClientKeyMiddleware  ──  validates X-Client-Key
      │
      ▼
ReviewsController    ──  verifies X-Ado-Token (identity check only), returns 202 + jobId
      │
      ▼
InMemoryJobRepository  (ConcurrentDictionary, lost on restart)
      │
      ▼  polled every 2 s
ReviewJobWorker (BackgroundService)
      │
      ▼
ReviewOrchestrationService
      ├── AdoPullRequestFetcher  ──►  Azure DevOps  (fetches changed files via service identity)
      ├── AgentAiReviewCore      ──►  Azure OpenAI / Foundry  (Responses API)
      └── AdoCommentPoster       ──►  Azure DevOps  (posts review threads)
```

The caller's `X-Ado-Token` is used **only** to verify that the caller has access to the ADO
organisation. The backend uses its own Azure credential (`DefaultAzureCredential` /
`ClientSecretCredential`) to fetch PR content and post comments.

---

## Project structure

```
src/
├── MeisterProPR.Domain/          # Entities, value objects — zero NuGet deps
├── MeisterProPR.Application/     # Use cases, service interfaces, DTOs
├── MeisterProPR.Infrastructure/  # ADO client, AI client, in-memory repository
└── MeisterProPR.Api/             # Controllers, middleware, workers, Program.cs
tests/
├── MeisterProPR.Domain.Tests/
├── MeisterProPR.Application.Tests/
├── MeisterProPR.Infrastructure.Tests/
└── MeisterProPR.Api.Tests/
```

Dependency rule: `Api → Application → Domain ← Infrastructure`
Infrastructure references are only wired up in `Program.cs`.

---

## Quick start

```bash
# Set required config (recommended: dotnet user-secrets)
dotnet user-secrets set "AI_ENDPOINT"         "https://<resource>.openai.azure.com/" --project src/MeisterProPR.Api
dotnet user-secrets set "AI_DEPLOYMENT"       "gpt-4o"                               --project src/MeisterProPR.Api
dotnet user-secrets set "MEISTER_CLIENT_KEYS" "my-secret-key"                        --project src/MeisterProPR.Api

# Run
dotnet run --project src/MeisterProPR.Api

# Verify
curl http://localhost:5000/healthz
```

See [GETTING_STARTED.md](GETTING_STARTED.md) for full setup instructions, including Azure
permissions, service principal configuration, Docker, and API usage examples.

---

## Tech stack

| Layer          | Technology                                                  |
|----------------|-------------------------------------------------------------|
| Runtime        | .NET 10 / ASP.NET Core MVC                                  |
| AI client      | `Microsoft.Extensions.AI` + Azure OpenAI Responses API      |
| ADO client     | `Microsoft.TeamFoundationServer.Client`                     |
| Auth           | `Azure.Identity` (`DefaultAzureCredential` / SP)            |
| Logging        | Serilog (structured JSON in production)                     |
| Observability  | OpenTelemetry OTLP traces + Prometheus metrics              |
| Tests          | xUnit + NSubstitute + `WebApplicationFactory`               |
| Container      | Linux rootless (`mcr.microsoft.com/dotnet/aspnet:10.0`)     |

---

## Key environment variables

| Variable              | Required | Description                                         |
|-----------------------|----------|-----------------------------------------------------|
| `MEISTER_CLIENT_KEYS` | Yes      | Comma-separated valid client keys (`X-Client-Key`)  |
| `AI_ENDPOINT`         | Yes      | Azure OpenAI or AI Foundry endpoint URL             |
| `AI_DEPLOYMENT`       | Yes      | Model deployment name (e.g. `gpt-4o`)               |
| `AZURE_CLIENT_ID`     | Dev      | Service principal app ID                            |
| `AZURE_TENANT_ID`     | Dev      | Azure AD tenant ID                                  |
| `AZURE_CLIENT_SECRET` | Dev      | Service principal secret — **never commit**         |
| `AI_API_KEY`          | No       | AI API key; omit to use `DefaultAzureCredential`    |

---

## Running tests

```bash
dotnet test
```

All tests use in-memory stubs and `WebApplicationFactory` — no real Azure credentials needed.

---

## Docker

```bash
docker compose up --build
```

The service listens on port `8080` inside the container. Supply variables via a `.env` file at
the repo root (see [GETTING_STARTED.md](GETTING_STARTED.md#running-with-docker)).
