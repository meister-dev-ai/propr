# Tasks: MVP Backend — Local AI Code Review

**Input**: Design documents from `specs/001-mvp-backend/`
**Prerequisites**: plan.md ✅ spec.md ✅ research.md ✅ data-model.md ✅ contracts/ ✅ quickstart.md ✅

**Tests**: TDD is **NON-NEGOTIABLE** per Constitution Principle II. Every `[TEST]`
task MUST be confirmed failing (red) before the corresponding implementation task begins.
Red → Green → Refactor is strictly enforced.

**Organization**: Tasks are grouped by user story to enable independent
implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story (US1–US4) this task belongs to
- Exact file paths are provided in every task description

## Path Conventions

- **API layer**: `src/MeisterProPR.Api/`
- **Application layer**: `src/MeisterProPR.Application/`
- **Domain layer**: `src/MeisterProPR.Domain/`
- **Infrastructure layer**: `src/MeisterProPR.Infrastructure/`
- **Tests**: `tests/MeisterProPR.{Layer}.Tests/`

---

## Phase 1: Setup

**Purpose**: Create the .NET 10 solution skeleton. Nothing is runnable yet.

- [X] T001 Create .NET 10 solution: `dotnet new sln -n MeisterProPR`; scaffold four source
  projects (`dotnet new classlib`/`webapi`) — `MeisterProPR.Domain`, `MeisterProPR.Application`, `MeisterProPR.Infrastructure`, `MeisterProPR.Api`
  under `src/`; scaffold four test projects (`dotnet new xunit`) under `tests/MeisterProPR.{Layer}.Tests/`; add all
  eight projects to the solution
- [X] T002 Configure project references to enforce inward-only
  dependencies: `Api → Application`, `Api → Infrastructure` (Program.cs DI
  only), `Application → Domain`, `Infrastructure → Domain`; verify `Infrastructure` has NO reference to `Api`;
  verify `Application` has NO reference to `Infrastructure`
- [X] T003 [P] Add NuGet packages to each project per `research.md` Decision 6: Infrastructure
  gets `Microsoft.Extensions.AI`, `Azure.AI.OpenAI` (prerelease), `Microsoft.Agents.AI.OpenAI` (
  prerelease), `Azure.Identity`, `Microsoft.TeamFoundationServer.Client`, `Microsoft.VisualStudio.Services.Client`, `DiffPlex`;
  Api gets `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `OpenTelemetry.*`, `Swashbuckle.AspNetCore`; all test projects
  get `xunit`, `xunit.runner.visualstudio`, `NSubstitute`, `Microsoft.AspNetCore.Mvc.Testing` (Api.Tests only)
- [X] T004 [P] Add `global.json` at repository root pinning `sdk.version` to .NET 10;
  add `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
  and `<ServerGarbageCollection>true</ServerGarbageCollection>` to each `src/` project's `.csproj`;
  add `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` to all projects
- [X] T005 [P] Create `.gitignore` at repository root
  covering `bin/`, `obj/`, `.env`, `*.user`, `*.suo`, `.vs/`, `TestResults/`
- [X] T006 Create `src/MeisterProPR.Api/appsettings.json` with non-secret defaults only: Serilog minimum
  level `Information`, `OTLP_ENDPOINT` placeholder `""`, `Serilog.WriteTo` console for
  Development; `appsettings.Development.json` with `Serilog.MinimumLevel.Default: Debug`
- [X] T054 [P] Correct `X-Ado-Token` description in `openapi.json` at repository root (all three path operations):
  change from "used by the backend to fetch PR data and post review comments" to "ADO token used solely to verify the
  requesting user is an authenticated ADO organisation member; the backend uses its own managed identity for all ADO API
  operations; this token is never stored, forwarded, or logged"

**Checkpoint**: `dotnet build` passes with no errors across all eight projects

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain model, application interfaces, security middleware, and core
infrastructure that every user story depends on. No user story implementation
begins until this phase is complete and all tests are green.

**⚠️ CRITICAL**: Write tests first, confirm they FAIL, then implement.

### Domain — Tests

- [X] T007 [P] [TEST] Write failing unit tests for domain enums (`JobStatus`, `ChangeType`, `CommentSeverity`) — verify
  all enum values exist and have correct names — in `tests/MeisterProPR.Domain.Tests/Enums/EnumTests.cs`
- [X] T008 [P] [TEST] Write failing unit tests for `ReviewJob` entity: constructor sets all fields, `Status` defaults
  to `Pending`, `CompletedAt` is null on creation, `Id` is non-empty Guid —
  in `tests/MeisterProPR.Domain.Tests/Entities/ReviewJobTests.cs`
- [X] T009 [P] [TEST] Write failing unit tests for value objects: `ReviewComment` requires
  non-null `Severity`/`Message`, `ChangedFile` requires non-null `Path`/`ChangeType` —
  in `tests/MeisterProPR.Domain.Tests/ValueObjects/`

### Domain — Implementation

- [X] T010 [P] Create `JobStatus`, `ChangeType`, `CommentSeverity` enums in `src/MeisterProPR.Domain/Enums/` — make T007
  pass
- [X] T011 [P] Create `ReviewJob` entity in `src/MeisterProPR.Domain/Entities/ReviewJob.cs` with all fields
  from `data-model.md`; include `ClientKey` property — make T008 pass
- [X] T012 [P] Create value
  objects `PullRequest.cs`, `ChangedFile.cs`, `ReviewResult.cs`, `ReviewComment.cs`, `ClientRegistration.cs`
  in `src/MeisterProPR.Domain/ValueObjects/` — make T009 pass
- [X] T013 Create `IAiReviewCore` interface
  in `src/MeisterProPR.Domain/Interfaces/IAiReviewCore.cs`: `Task<ReviewResult> ReviewAsync(PullRequest pullRequest, CancellationToken cancellationToken = default)` —
  no AI SDK types anywhere in this file

### Application — Interfaces

- [X] T014 [P] Create `IJobRepository` in `src/MeisterProPR.Application/Interfaces/IJobRepository.cs` with all methods
  from `data-model.md`: `FindActiveJob`, `Add`, `GetById`, `GetAllForClient`, `TryTransition`, `SetResult`, `SetFailed`
- [X] T015 [P] Create `IClientRegistry`, `IPullRequestFetcher`, `IAdoCommentPoster`, `IAdoTokenValidator`
  in `src/MeisterProPR.Application/Interfaces/`

### Infrastructure — InMemoryJobRepository

- [X] T016 [TEST] Write failing unit tests for `InMemoryJobRepository` covering: `Add` + `GetById`
  round-trip; `FindActiveJob` returns existing non-Failed job; `FindActiveJob` returns null for Failed
  job; `TryTransition` succeeds on valid transition, returns false on invalid; `GetAllForClient` returns
  newest-first; `SetResult` and `SetFailed` populate fields —
  in `tests/MeisterProPR.Infrastructure.Tests/Repositories/InMemoryJobRepositoryTests.cs`
- [X] T017 Implement `InMemoryJobRepository` in `src/MeisterProPR.Infrastructure/Repositories/InMemoryJobRepository.cs`
  using `ConcurrentDictionary<Guid, ReviewJob>`; implement `FindActiveJob` idempotency check;
  implement `GetAllForClient` with descending `SubmittedAt` order — make T016 pass

### Infrastructure — EnvVarClientRegistry

- [X] T018 [P] [TEST] Write failing unit tests for `EnvVarClientRegistry`: valid key returns `true`; unknown key
  returns `false`; empty `MEISTER_CLIENT_KEYS` causes startup exception —
  in `tests/MeisterProPR.Infrastructure.Tests/Configuration/EnvVarClientRegistryTests.cs`
- [X] T019 [P] Implement `EnvVarClientRegistry`
  in `src/MeisterProPR.Infrastructure/Configuration/EnvVarClientRegistry.cs`; read `MEISTER_CLIENT_KEYS` env var; split
  on comma; throw `InvalidOperationException` if empty or not set — make T018 pass

### API — Security Middleware

- [X] T020 [TEST] Write failing integration tests using `WebApplicationFactory<Program>`: request with no `X-Client-Key`
  header returns `401`; request with invalid `X-Client-Key` returns `401`; request with valid `X-Client-Key` passes
  through to controller — in `tests/MeisterProPR.Api.Tests/Middleware/ClientKeyMiddlewareTests.cs`
- [X] T021 Implement `ClientKeyMiddleware` in `src/MeisterProPR.Api/Middleware/ClientKeyMiddleware.cs`;
  call `IClientRegistry.IsValidKey`; return `401 Unauthorized` immediately if invalid, no business logic executes — make
  T020 pass

### API — Observability + DI Wiring

- [X] T022 Configure Serilog in `src/MeisterProPR.Api/Program.cs`: JSON sink in non-Development environments; Console
  sink in Development; enrich with `FromLogContext` and `WithProperty("Application", "MeisterProPR")`; add destructuring
  policies to scrub sensitive fields from structured log output: mask `X-Client-Key`, `X-Ado-Token`,
  and `AZURE_CLIENT_SECRET` values before any log sink receives them
- [X] T023 [P] Create `ReviewJobTelemetry` static class in `src/MeisterProPR.Api/Telemetry/ReviewJobTelemetry.cs`
  with `ActivitySource("MeisterProPR.ReviewJobs", "1.0.0")`; register it with `AddOpenTelemetry().WithTracing(...)` (
  OTLP exporter, AspNetCore instrumentation) and `WithMetrics(...)` (Prometheus exporter) in `Program.cs`
- [X] T024 [P] Configure Swashbuckle in `Program.cs`: `AddSwaggerGen` with XML doc file paths from all four src
  projects; configure `JsonStringEnumConverter` with `CamelCase` naming policy;
  add `SecurityDefinition("clientKey", ...)` for `X-Client-Key` header
- [X] T025 Create `InfrastructureServiceExtensions.cs`
  in `src/MeisterProPR.Infrastructure/DependencyInjection/`: `AddInfrastructure(IServiceCollection, IConfiguration)`
  registers `IJobRepository` → `InMemoryJobRepository` (singleton), `IClientRegistry` → `EnvVarClientRegistry` (
  singleton), `IAdoTokenValidator` → `AdoTokenValidator` (singleton), `IPullRequestFetcher` → `AdoPullRequestFetcher` (
  scoped), `IAdoCommentPoster` → `AdoCommentPoster` (scoped), `IAiReviewCore` → `AgentAiReviewCore` (
  singleton), `IChatClient` → Azure OpenAI via Agent Framework (singleton); also registers `VssConnectionFactory` (
  singleton)
- [X] T026 Complete `Program.cs` DI wiring: call `builder.Services.AddInfrastructure(...)`,
  register `ReviewOrchestrationService`, add `ClientKeyMiddleware` to pipeline, register `ReviewJobWorker`
  as `IHostedService`, configure `HostOptions.ShutdownTimeout = TimeSpan.FromMinutes(3)`, add `IHttpClientFactory` with
  named client `"AdoTokenValidator"`

**Checkpoint**: `dotnet test tests/MeisterProPR.Domain.Tests tests/MeisterProPR.Infrastructure.Tests` — all pass (green)

---

## Phase 3: User Story 4 — Run Backend Locally (Priority: P1)

**Goal**: The backend starts from env vars alone, exposes `/healthz`, and rejects
startup if required configuration is missing.

**Independent Test** (from spec.md): Set env vars, `dotnet run --project src/MeisterProPR.Api`,
call `GET /healthz` → `200 OK`.

- [X] T027 [TEST] [US4] Write failing unit test: when `AI_ENDPOINT` is missing, `Program.cs` startup
  throws `InvalidOperationException` with a clear message containing "AI_ENDPOINT" —
  in `tests/MeisterProPR.Api.Tests/StartupValidationTests.cs`
- [X] T028 [TEST] [US4] Write failing integration test using `WebApplicationFactory<Program>`: `GET /healthz`
  returns `200 OK` with JSON body `{"status":"Healthy"}` — in `tests/MeisterProPR.Api.Tests/HealthCheckTests.cs`
- [X] T029 [US4] Add startup env var validation in `Program.cs`: throw `InvalidOperationException` if `AI_ENDPOINT`
  or `AI_DEPLOYMENT` or `MEISTER_CLIENT_KEYS` is missing/empty — make T027 pass
- [X] T030 [US4] Implement `WorkerHealthCheck` in `src/MeisterProPR.Api/HealthChecks/WorkerHealthCheck.cs`
  implementing `IHealthCheck`; check background worker is running; return `Healthy` when worker is alive
- [X] T031 [US4] Wire `app.MapHealthChecks("/healthz")` with `WorkerHealthCheck` in `Program.cs`; add health check is
  excluded from `ClientKeyMiddleware` (no `X-Client-Key` required on `/healthz`) — make T028 pass

**Checkpoint**: `dotnet run --project src/MeisterProPR.Api` starts successfully with
all env vars set; `GET /healthz` returns `200 OK`

---

## Phase 4: User Story 1 + User Story 2 — Submit Review & Poll Results (Priority: P1) 🎯 MVP

**Goal**: A developer submits a PR for review and polls until the result is returned.
Comments are posted back to the ADO pull request.

**Independent Test** (from spec.md): Submit `POST /reviews` → receive `202 jobId` →
poll `GET /reviews/{jobId}` until `"status": "completed"` → result contains summary
and comments; ADO PR shows inline and PR-level comment threads.

**⚠️ CRITICAL TDD**: Write ALL test tasks in this phase first. Confirm they are
failing before starting any implementation task.

### Tests — Infrastructure (T032–T037)

- [X] T032 [P] [TEST] [US1] Write failing unit tests for `AdoTokenValidator`: valid token (200 from mocked
  connectionData endpoint) returns `true`; 401 response returns `false`; HTTP exception propagates —
  in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoTokenValidatorTests.cs`
- [X] T033 [P] [TEST] [US1] Write failing unit tests for `VssConnectionFactory`: returns a `VssConnection`
  when `DefaultAzureCredential` provides a token; uses ADO resource
  scope `499b84ac-1321-427f-aa17-267ca6975798/.default` —
  in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/VssConnectionFactoryTests.cs`
- [X] T034 [P] [TEST] [US1] Write failing unit tests for `AdoPullRequestFetcher` with mocked `GitHttpClient`:
  returns `PullRequest` with correct `ChangedFiles`; file content is fetched at head commit; diff is generated for
  edited files; deleted files have empty `FullContent` and non-empty `UnifiedDiff`; empty iteration
  returns `PullRequest` with empty `ChangedFiles` —
  in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoPullRequestFetcherTests.cs`
- [X] T035 [P] [TEST] [US1] Write failing unit tests for `AgentAiReviewCore` with mocked `IChatClient`: valid JSON
  response parses to `ReviewResult`; malformed JSON throws; `CancellationToken` is forwarded —
  in `tests/MeisterProPR.Infrastructure.Tests/AI/AgentAiReviewCoreTests.cs`
- [X] T036 [P] [TEST] [US1] Write failing unit tests for `AdoCommentPoster` with mocked `GitHttpClient`: inline comment
  calls `CreateThreadAsync` with non-null `ThreadContext`; PR-level comment calls `CreateThreadAsync` with
  null `ThreadContext`; empty `comments` list results in zero `CreateThreadAsync` calls; summary-only comment is posted
  as PR-level — in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoCommentPosterTests.cs`
- [X] T037 [TEST] [US1] Write failing unit tests for `ReviewOrchestrationService` with NSubstituted deps: successful
  flow transitions job to `Completed` and stores `ReviewResult`; ADO fetch exception transitions job to `Failed`; AI
  exception transitions job to `Failed`; comment post exception transitions job to `Failed` (review result still set) —
  in `tests/MeisterProPR.Application.Tests/Services/ReviewOrchestrationServiceTests.cs`

### Tests — API Layer (T038–T040)

- [X] T038 [TEST] [US1] Write failing integration tests for `POST /reviews` using `WebApplicationFactory`: valid request
  returns `202` with `jobId` UUID; missing `X-Client-Key` returns `401`; invalid `X-Ado-Token` (mocked validator returns
  false) returns `401`; submitting same PR iteration twice returns original `jobId` (idempotency) —
  in `tests/MeisterProPR.Api.Tests/Controllers/ReviewsControllerPostTests.cs`
- [X] T039 [TEST] [US2] Write failing integration tests for `GET /reviews/{jobId}`: returns `200`
  with `"status":"pending"` for a new job; returns `200` with `"status":"completed"` and `result` payload for completed
  job; returns `200` with `"status":"failed"` and `error` string; unknown `jobId` returns `404` —
  in `tests/MeisterProPR.Api.Tests/Controllers/ReviewsControllerGetTests.cs`
- [X] T040 [P] [TEST] [US1] Write failing unit tests for `ReviewJobWorker`: pending job is claimed
  and `ProcessJobSafeAsync` fires; unhandled exception inside processor transitions job to `Failed` (worker does not
  crash); `OperationCanceledException` from stoppingToken reverts job to `Pending`; in-flight tasks are awaited on
  shutdown — in `tests/MeisterProPR.Api.Tests/Workers/ReviewJobWorkerTests.cs`

### Implementation — Infrastructure (T041–T046)

- [X] T041 [P] [US1] Implement `AdoTokenValidator`
  in `src/MeisterProPR.Infrastructure/AzureDevOps/AdoTokenValidator.cs`: inject `IHttpClientFactory`;
  call `GET https://app.vssps.visualstudio.com/_apis/connectionData?api-version=7.1`
  with `Authorization: Bearer {token}`; return `true` on `200`, `false` on `401` — make T032 pass
- [X] T042 [P] [US1] Implement `VssConnectionFactory`
  in `src/MeisterProPR.Infrastructure/AzureDevOps/VssConnectionFactory.cs`:
  inject `TokenCredential` (`DefaultAzureCredential`); acquire token with
  scope `499b84ac-1321-427f-aa17-267ca6975798/.default`;
  return `new VssConnection(new Uri(orgUrl), new VssOAuthAccessTokenCredential(token.Token))`; singleton caches
  connection and refreshes before expiry — make T033 pass
- [X] T043 [P] [US1] Implement `ReviewPrompts` static class
  in `src/MeisterProPR.Infrastructure/AI/ReviewPrompts.cs`: `SystemPrompt` string (act as code reviewer, respond with
  JSON only per schema in research.md); `BuildUserMessage(PullRequest pr)` formats PR metadata + all changed files
  with `=== FILE === / --- FULL CONTENT --- / --- DIFF ---` structure
- [X] T044 [US1] Implement `AdoPullRequestFetcher`
  in `src/MeisterProPR.Infrastructure/AzureDevOps/AdoPullRequestFetcher.cs`:
  inject `VssConnectionFactory`; `GetPullRequestIterationAsync` for source/base commit
  SHAs; `GetPullRequestIterationChangesAsync` for changed file list; `GetItemContentAsync` with `GitVersionDescriptor`
  for file content; `DiffPlex.InlineDiffBuilder.BuildDiffModel` to generate unified diff; handle deleted files (empty
  head content) and added files (empty base content); wrap ADO calls in `Activity` spans
  from `ReviewJobTelemetry.Source` — make T034 pass
- [X] T045 [US1] Implement `AgentAiReviewCore` in `src/MeisterProPR.Infrastructure/AI/AgentAiReviewCore.cs`:
  inject `IChatClient`; build `List<ChatMessage>` with `ChatRole.System` + `ChatRole.User`;
  call `chatClient.CompleteAsync(messages, cancellationToken: ct)`; parse `response.Message.Text`
  with `System.Text.Json` into `ReviewResultDto`; map to domain `ReviewResult`; wrap AI call in `Activity` span — make
  T035 pass
- [X] T046 [P] [US1] Implement `AdoCommentPoster` in `src/MeisterProPR.Infrastructure/AzureDevOps/AdoCommentPoster.cs`:
  inject `VssConnectionFactory`; for each `ReviewComment` with `FilePath` != null: `CreateThreadAsync`
  with `ThreadContext.FilePath` + `RightFileStart` (when `LineNumber` != null) or file-only anchor; for comments
  with `FilePath` == null: `CreateThreadAsync` with `ThreadContext = null`; post summary as PR-level thread; all calls
  via `GitHttpClient`; wrap in `Activity` span — make T036 pass

### Implementation — Application (T047)

- [X] T047 [US1] Implement `ReviewOrchestrationService`
  in `src/MeisterProPR.Application/Services/ReviewOrchestrationService.cs`:
  inject `IJobRepository`, `IPullRequestFetcher`, `IAiReviewCore`, `IAdoCommentPoster`, `ILogger<ReviewOrchestrationService>`; `ProcessAsync(ReviewJob job, CancellationToken ct)`:
  call `prFetcher.FetchAsync` → `aiCore.ReviewAsync` → `commentPoster.PostAsync` → `jobs.SetResult` + transition
  to `Completed`; on any exception: `jobs.SetFailed` + log error; use structured log messages with `JobId`, `PrId`
  properties — make T037 pass

### Implementation — Background Worker (T048)

- [X] T048 [US1] Implement `ReviewJobWorker` in `src/MeisterProPR.Api/Workers/ReviewJobWorker.cs`:
  extend `BackgroundService`; `PeriodicTimer` (2-second interval); loop calls `IJobRepository.ClaimPendingJobs()` (
  TryTransition Pending → Processing); each claimed job launches `ProcessJobSafeAsync` as tracked task
  in `ConcurrentDictionary<Guid, Task>`; `ProcessJobSafeAsync` wraps `ReviewOrchestrationService.ProcessAsync` in
  try/catch: `OperationCanceledException` → revert to Pending, all other exceptions → SetFailed; start/stop wrapped
  in `ActivitySource` span; `ExecuteAsync` awaits `Task.WhenAll` before returning on cancellation — make T040 pass

### Implementation — API Endpoints (T049–T051)

- [X] T049 [US1] Implement `POST /reviews` action in `src/MeisterProPR.Api/Controllers/ReviewsController.cs`:
  validate `X-Ado-Token` via `IAdoTokenValidator` (401 if invalid); call `IJobRepository.FindActiveJob` (return
  existing `jobId` if found); create new `ReviewJob` with `Pending` status and client key
  from `X-Client-Key`; `IJobRepository.Add`; return `202 Accepted` with `{ "jobId": job.Id }`; add complete XML
  docs (`<summary>`, `<param>`, `<response>`) — make T038 pass
- [X] T050 [US2] Implement `GET /reviews/{jobId}` action in `src/MeisterProPR.Api/Controllers/ReviewsController.cs`:
  validate `X-Ado-Token`; `IJobRepository.GetById` → `404` if not found; map `ReviewJob` to `ReviewStatusResponse` DTO (
  include `result` when Completed, `error` when Failed); return `200 OK`; add XML docs — make T039 pass
- [X] T051 [P] [US1] Add `[LoggerMessage]`-attributed log methods to `ReviewsController` for submit (info) and ADO token
  rejection (warning, no token value logged); add Activity span tag `job.id` on submit

**Checkpoint**: `POST /reviews` → `202`, poll `GET /reviews/{jobId}` → `completed`
with AI review result. All tests in Phase 4 are green. `dotnet test` passes.

---

## Phase 5: User Story 3 — List Review History (Priority: P2)

**Goal**: `GET /reviews` returns all jobs for the current client key, newest first.

**Independent Test** (from spec.md): Submit two reviews, call `GET /reviews` →
array with both jobs in descending `submittedAt` order; `GET /reviews` with no
prior submissions returns empty array.

- [X] T052 [TEST] [US3] Write failing integration tests for `GET /reviews`: returns `200` with array
  of `ReviewListItem`; newest job is first; empty array when no jobs submitted; `X-Client-Key` scoping (jobs from one
  key not visible to another key) — in `tests/MeisterProPR.Api.Tests/Controllers/ReviewsControllerListTests.cs`
- [X] T053 [US3] Implement `GET /reviews` action in `src/MeisterProPR.Api/Controllers/ReviewsController.cs`:
  validate `X-Ado-Token`; call `IJobRepository.GetAllForClient(clientKey)` where `clientKey` is from
  validated `X-Client-Key` header; map to `ReviewListItem[]` DTO; return `200 OK`; add XML docs — make T052 pass

**Checkpoint**: All three API endpoints functional and tested independently

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Observability hardening, containerisation, and CI gate.

- [X] T056 [P] Add `System.Diagnostics.Metrics` instrumentation: `Meter("MeisterProPR")` with `ObservableGauge` for job
  queue depth (pending count), `Histogram` for job processing duration, registered in `Program.cs`; export via
  Prometheus endpoint (`/metrics`)
- [X] T057 [P] Create `Dockerfile` at repository root: multi-stage build using `mcr.microsoft.com/dotnet/sdk:10.0` (
  build stage) and `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime stage); non-root
  user; `EXPOSE 8080`; `ENTRYPOINT ["dotnet", "MeisterProPR.Api.dll"]`
- [X] T058 [P] Create `docker-compose.yml` at repository root: `meisterpropr` service built from `Dockerfile`;
  environment section with all required env vars as `${VAR_NAME}` references; port mapping `8080:8080`; `healthcheck`
  using `/healthz`
- [X] T059 Verify full CI gate: `dotnet test` runs all four test projects with no real HTTP calls leaving the process;
  confirm `WebApplicationFactory` tests use in-memory implementations; confirm no test has a hard dependency
  on `AZURE_*` or `AI_ENDPOINT` env vars

---

## Dependencies

```
Phase 1 (T001–T006, T054)
  └─► Phase 2 (T007–T026) — domain + interfaces + security + DI wiring
        └─► Phase 3 (T027–T031) — US4: /healthz + startup validation
              └─► Phase 4 (T032–T051) — US1+US2: submit + poll
                    ├─► Phase 5 (T052–T053) — US3: list history
                    └─► Phase 6 (T056–T059) — Polish

Within Phase 2:
  T007–T009 (domain tests) → T010–T013 (domain impl)
  T010–T013 → T014–T015 (application interfaces)
  T014–T015 → T016–T019 (infrastructure: repo + registry)
  T016–T019 → T020–T021 (middleware tests + impl)
  T022–T026 (observability + DI) can run in parallel with T016–T021 once T014–T015 done

Within Phase 4:
  T032–T040 (all tests) can be written in parallel before any implementation
  T041–T046 (infrastructure impls) can run in parallel after their test tasks
  T047 (ReviewOrchestrationService) depends on T041–T046 being complete
  T048 (ReviewJobWorker) depends on T047
  T049–T051 (controller) depends on T047 + T048
```

---

## Parallel Execution Opportunities

**Phase 2 (once T001 done)**:

- T007, T008, T009 — domain test files (parallel)
- T010, T011, T012 — domain impl files (parallel, after tests written)
- T014, T015 — application interfaces (parallel)
- T018, T019 — client registry tests + impl (parallel with T016, T017)
- T023, T024, T025 — observability config (parallel)

**Phase 4 (once T026 done)**:

- T032, T033, T034, T035, T036 — infrastructure tests (all parallel)
- T040 — worker test (parallel with above)
- T038, T039 — API integration tests (parallel with above)
- T041, T042 — ADO validator + connection factory impl (parallel)
- T043 — ReviewPrompts (parallel with T041/T042)
- T046 — AdoCommentPoster (parallel with T044/T045 once T043 done)

---

## Implementation Strategy (MVP Scope)

**Minimum viable delivery** = Phases 1 + 2 + 3 + 4 (T001–T051)

This delivers User Stories 1, 2, and 4: a developer can start the backend
locally, submit a PR for review, and poll until the result is returned with
comments posted to ADO. US3 (list history) and polish are incremental additions.

**Suggested delivery order**:

1. T001–T006, T054 (setup + contract correction) → verify `dotnet build` green
2. T007–T026 (foundation) → verify `dotnet test` Domain + Infrastructure green
3. T027–T031 (US4) → verify `GET /healthz` works locally
4. T032–T051 (US1+US2) → end-to-end review cycle complete
5. T052–T053 (US3) → list endpoint
6. T056–T059 (polish) → production-ready

---

## Format Validation

All tasks follow the required format:
`- [X] [TaskID] [P?] [Story?] Description with file path`

- Total tasks: **58**
- Phase 1 (Setup): 7 tasks (T001–T006, T054)
- Phase 2 (Foundational): 20 tasks (T007–T026)
- Phase 3 (US4): 5 tasks (T027–T031)
- Phase 4 (US1+US2): 20 tasks (T032–T051)
- Phase 5 (US3): 2 tasks (T052–T053)
- Phase 6 (Polish): 4 tasks (T056–T059)
- [TEST] tasks: 17 (T007–T009, T016, T018, T020, T027–T028, T032–T040, T052)
- [P] tasks (parallelizable): 25+
- User story labels: US1 on 20 tasks, US2 on 2, US3 on 2, US4 on 5
