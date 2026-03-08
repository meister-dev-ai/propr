# Implementation Plan: MVP Backend — Local AI Code Review

**Branch**: `001-mvp-backend` | **Date**: 2026-03-03 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/001-mvp-backend/spec.md`

## Summary

Build the Meister ProPR backend from scratch as a .NET 10 ASP.NET Core MVC
service using four-layer Clean Architecture. The service accepts PR review
requests via a REST API, fetches changed files from Azure DevOps (as the
backend's own managed identity), runs an AI review through the Microsoft Agent
Framework (`Microsoft.Agents.AI.OpenAI` / `Microsoft.Extensions.AI.IChatClient`),
posts findings back to the ADO pull request as inline and general comment threads,
and returns results via a polling endpoint. All state is kept in-memory; no
database or external broker is required to start locally.

---

## Technical Context

**Language/Version**: C# / .NET 10, TFM `net10.0`
**Primary Dependencies**: ASP.NET Core MVC, Microsoft Agent Framework
(`Microsoft.Agents.AI.OpenAI`), Microsoft.Extensions.AI (`IChatClient`),
Azure.AI.OpenAI, Azure.Identity (`DefaultAzureCredential`),
Microsoft.TeamFoundationServer.Client, Microsoft.VisualStudio.Services.Client,
DiffPlex, Serilog, OpenTelemetry, Swashbuckle
**Storage**: In-memory (`ConcurrentDictionary`) — no database or external service
**Testing**: xUnit + NSubstitute (Moq prohibited); `WebApplicationFactory<Program>` for integration tests
**Target Platform**: Linux rootless container (`mcr.microsoft.com/dotnet/aspnet:10.0`); local via `dotnet run`
**Project Type**: Web service (ASP.NET Core MVC)
**Performance Goals**: POST /reviews → 202 within 1 s (SC-002); review completion ≤ 120 s for ≤ 20 files (SC-003);
/healthz < 200 ms
**Constraints**: No Windows-specific APIs; config via env vars only; no external services required to start
**Scale/Scope**: Single-tenant MVP; in-memory state (lost on restart is acceptable); unbounded job concurrency

---

## Constitution Check

*Pre-research gate: PASS. Post-design re-check: PASS.*

- [x] **I. API-Contract-First** — This feature establishes the initial contract.
  `openapi.json` is already present at the repo root and defines all endpoints.
  The only change required is correcting the `X-Ado-Token` description from
  "used by the backend to fetch PR data" → "used solely for identity
  verification" — a **non-breaking** description update (no TypeScript client
  regeneration needed). All controller actions will carry complete XML docs.
  Swashbuckle regenerates `openapi.json` on every build.

- [x] **II. Test-First** — Enforced by the speckit workflow: `[TEST]` tasks
  appear first in `tasks.md`, confirmed failing before any implementation begins.
  xUnit + NSubstitute throughout. Integration tests use `WebApplicationFactory`
  with in-memory repository stub implementations — no real network calls.

- [x] **III. Container-First** — No Windows-specific APIs anywhere. All
  configuration from env vars (`MEISTER_CLIENT_KEYS`, `AI_ENDPOINT`,
  `AI_DEPLOYMENT`, `AZURE_*`). `/healthz` implemented via ASP.NET Core Health
  Checks. `docker-compose.yml` at repository root. Base images:
  `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime),
  `mcr.microsoft.com/dotnet/sdk:10.0` (build stage).

- [x] **IV. Clean Architecture** — Four projects with strictly inward-pointing
  dependencies (see Project Structure). `IAiReviewCore` is placed in Domain
  per FR-004's explicit mandate (see Complexity Tracking). All repository and
  registry interfaces are in Application. Controllers are thin orchestrators;
  all business logic is in Application services.

- [x] **V. Security** — `X-Client-Key` validated by `ClientKeyMiddleware` (runs
  before any controller). `X-Ado-Token` used only in `IAdoTokenValidator`,
  never passed to a `VssConnection`, never logged, never stored beyond the
  request. AI credentials (`AI_ENDPOINT`, `AI_DEPLOYMENT`, `AZURE_*`) come
  exclusively from env vars. Serilog destructuring policies scrub
  `X-Client-Key`, `X-Ado-Token`, `AZURE_CLIENT_SECRET`.

- [x] **VI. Job Reliability** — `IJobRepository` backed by `ConcurrentDictionary`.
  Job written as `Pending` and made visible before `202 Accepted` is returned.
  Status transitions enforced: `Pending → Processing → Completed | Failed`
  only. `ReviewJobWorker` catches all exceptions; failed jobs are marked
  `Failed` with `ex.Message`; worker never crashes the host. Idempotency key:
  `(organizationUrl, projectId, repositoryId, pullRequestId, iterationId)`.

- [x] **VII. Observability** — Serilog JSON sink (non-Development), Console sink
  (Development). `ActivitySource "MeisterProPR.ReviewJobs"` wraps each job
  execution, each ADO API call, and each AI call with OTel spans carrying
  `job.id`, `pr.id`, `repository.id`. `/healthz` reports worker liveness.
  `System.Diagnostics.Metrics` exports queue depth, job processing duration,
  and request duration in Prometheus-compatible format.

---

## Project Structure

### Documentation (this feature)

```text
specs/001-mvp-backend/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   └── openapi-notes.md # Phase 1 output — contract corrections and notes
└── tasks.md             # Phase 2 output (/speckit.tasks — NOT created here)
```

### Source Code (repository root)

```text
src/
├── MeisterProPR.Domain/                    # Zero external NuGet dependencies
│   ├── Entities/
│   │   └── ReviewJob.cs
│   ├── ValueObjects/
│   │   ├── PullRequest.cs
│   │   ├── ChangedFile.cs
│   │   ├── ReviewResult.cs
│   │   ├── ReviewComment.cs
│   │   └── ClientRegistration.cs
│   ├── Enums/
│   │   ├── JobStatus.cs
│   │   ├── ChangeType.cs
│   │   └── CommentSeverity.cs
│   └── Interfaces/
│       └── IAiReviewCore.cs
│
├── MeisterProPR.Application/               # Use cases, service interfaces, DTOs
│   ├── Interfaces/
│   │   ├── IJobRepository.cs
│   │   ├── IClientRegistry.cs
│   │   ├── IPullRequestFetcher.cs
│   │   ├── IAdoCommentPoster.cs
│   │   └── IAdoTokenValidator.cs
│   └── Services/
│       └── ReviewOrchestrationService.cs
│
├── MeisterProPR.Infrastructure/            # Impls; depends on Domain only
│   ├── Repositories/
│   │   └── InMemoryJobRepository.cs
│   ├── Configuration/
│   │   └── EnvVarClientRegistry.cs
│   ├── AzureDevOps/
│   │   ├── VssConnectionFactory.cs
│   │   ├── AdoPullRequestFetcher.cs
│   │   ├── AdoCommentPoster.cs
│   │   └── AdoTokenValidator.cs
│   ├── AI/
│   │   ├── AgentAiReviewCore.cs
│   │   └── ReviewPrompts.cs
│   └── DependencyInjection/
│       └── InfrastructureServiceExtensions.cs
│
└── MeisterProPR.Api/                       # Controllers, middleware, workers
    ├── Controllers/
    │   └── ReviewsController.cs
    ├── Middleware/
    │   └── ClientKeyMiddleware.cs
    ├── Workers/
    │   └── ReviewJobWorker.cs
    ├── HealthChecks/
    │   └── WorkerHealthCheck.cs
    ├── Telemetry/
    │   └── ReviewJobTelemetry.cs
    ├── appsettings.json                    # Non-secret defaults only
    └── Program.cs                         # DI wiring; Infrastructure refs here only

tests/
├── MeisterProPR.Domain.Tests/
├── MeisterProPR.Application.Tests/
├── MeisterProPR.Infrastructure.Tests/
└── MeisterProPR.Api.Tests/

docker-compose.yml
Dockerfile
CLAUDE.md
openapi.json                               # Generated by Swashbuckle; committed
```

**Structure Decision**: Four-project .NET solution per Constitution Principle IV.
`tests/` mirrors `src/` with one test project per source project. Docker assets
at repository root. `openapi.json` at repository root (single source of truth).

---

## Key Design Decisions

### 1. AI Provider Architecture (`IChatClient` abstraction)

`AgentAiReviewCore` in Infrastructure depends on `Microsoft.Extensions.AI.IChatClient`
rather than `AzureOpenAIClient` directly. The concrete `IChatClient` implementation
(backed by Azure OpenAI via `Microsoft.Agents.AI.OpenAI`) is registered in
`Program.cs`. Swapping providers in the future requires changing only one DI
registration — no Infrastructure code changes.

```
IAiReviewCore (Domain)
  └── AgentAiReviewCore : IAiReviewCore (Infrastructure) ← IChatClient (MEA)
        IChatClient ← registered in Program.cs (initial: AzureOpenAI via Agent Framework)
```

### 2. `X-Ado-Token` — Identity Verification Only

The user's ADO token is validated by calling
`GET app.vssps.visualstudio.com/_apis/connectionData` with the token as a
`Bearer` header using a named `HttpClient` from `IHttpClientFactory`. The token
is never passed to a `VssConnection` and never available to the ADO SDK. If
validation fails (401), `ClientKeyMiddleware` returns 401 before creating a job.

### 3. ADO Identity and Comment Author

The backend connects to ADO via `DefaultAzureCredential`, which resolves to:

- **Local dev**: service principal (`AZURE_CLIENT_ID/TENANT_ID/CLIENT_SECRET`)
- **Production**: managed identity

Comments appear in ADO under the **Azure AD display name** of the credential
(e.g. *"Meister ProPR"*). The service principal's app registration must be
named accordingly and granted ADO `Reader` + `Contribute to pull requests`.

### 4. Unified Diff Generation

ADO `GitHttpClient` does not expose raw unified patch text. `DiffPlex.InlineDiffBuilder`
generates `+`/`-` diff lines from the base and head file content strings fetched
separately. This is passed alongside full file content to the AI (FR-003).

### 5. Background Worker Concurrency

`ReviewJobWorker` polls every 2 seconds with `PeriodicTimer`. Each claimed
`Pending` job is launched as a tracked `async Task` (fire-and-forget within a
`try/catch`). No concurrency cap (FR-014). In-flight tasks are tracked in a
`ConcurrentDictionary<Guid, Task>`; `Task.WhenAll` drains them on shutdown.
`HostOptions.ShutdownTimeout = 3 minutes`.

### 6. Idempotency

`IJobRepository.FindActiveJob(orgUrl, project, repo, prId, iterationId)` is
called atomically before creating a new job. If a non-`Failed` job exists for
the same PR iteration, its `Id` is returned immediately (FR-012).

---

## Complexity Tracking

| Deviation                                                               | Why Needed                                                                                                                                                                                                                                                               | Simpler Alternative Rejected Because                                                                                                                                                         |
|-------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `IAiReviewCore` in Domain (not Application)                             | FR-004 explicitly mandates it: "an `IAiReviewCore` interface defined in the domain layer". Placing it in Domain ensures the domain's review orchestration owns the AI contract with no SDK-type leakage; it has zero external dependencies so it is a legal Domain type. | Moving to Application would contradict the spec and would require the Application layer to own a contract that the Domain's review pipeline depends on — inverting the dependency direction. |
| `DiffPlex` third-party library in Infrastructure                        | ADO SDK does not provide raw unified diff text (`GetCommitDiffsAsync` returns structured `GitChange` objects, not patch text). FR-003 explicitly requires unified diff for accurate line-number attribution.                                                             | Reconstructing a minimal diff without a library is feasible but error-prone, harder to test, and would need to be maintained in perpetuity — not a simpler alternative in practice.          |
| `Microsoft.Extensions.AI.IChatClient` abstraction inside Infrastructure | The user requirement (not tied to OpenAI) mandates provider-agnostic AI access. `IChatClient` is the .NET standard for this and has adapters for all major providers.                                                                                                    | Depending directly on `AzureOpenAIClient` would require Infrastructure code changes for every provider swap, violating the spirit of the clean architecture.                                 |
