# Tasks: Add AI Identity as Optional Reviewer

**Input**: Design documents from `/specs/005-add-ai-reviewer/`
**Branch**: `005-add-ai-reviewer`
**Constitution**: TDD mandatory — `[TEST]` tasks MUST be written first and confirmed failing before implementation

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel (different files, no runtime dependencies)
- **[TEST]**: Write this test first — confirm it FAILS before implementing the production code
- **[US1/2/3]**: User story this task belongs to

---

## Phase 1: Setup (No new projects needed — existing solution modified)

_No setup tasks required — all changes are to existing projects._

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Database schema, interface contracts, and DTO changes that MUST be complete
before any user story can compile or run. All user story phases depend on this phase.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Tests (write first — confirm failing)

- [X] T001 [TEST] [P] Write failing unit test asserting `IClientRegistry.GetReviewerIdAsync` returns `null` for unknown client ID in `tests/MeisterProPR.Infrastructure.Tests/Repositories/ClientRegistryTests.cs`
- [X] T002 [TEST] [P] Write failing unit test asserting `EnvVarClientRegistry.GetReviewerIdAsync` always returns `null` in `tests/MeisterProPR.Infrastructure.Tests/Configuration/EnvVarClientRegistryTests.cs`
- [X] T003 [TEST] [P] Write failing compilation test confirming `CrawlConfigurationDto.ReviewerId` is `Guid?` (nullable) in `tests/MeisterProPR.Application.Tests/DTOs/CrawlConfigurationDtoTests.cs`

### Application layer — interfaces and DTOs

- [X] T004 [P] Add `Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default)` to `src/MeisterProPR.Application/Interfaces/IClientRegistry.cs`
- [X] T005 [P] Create `src/MeisterProPR.Application/Interfaces/IAdoReviewerManager.cs` with `AddOptionalReviewerAsync(string organizationUrl, string projectId, string repositoryId, int pullRequestId, Guid reviewerId, Guid? clientId, CancellationToken cancellationToken)` method
- [X] T006 [P] Change `ReviewerId` from `Guid` to `Guid?` in `src/MeisterProPR.Application/DTOs/CrawlConfigurationDto.cs`
- [X] T007 Remove `Guid reviewerId` parameter from `AddAsync` and `ExistsAsync` in `src/MeisterProPR.Application/Interfaces/ICrawlConfigurationRepository.cs`; update unique-existence check signature to `ExistsAsync(Guid clientId, string organizationUrl, string projectId, CancellationToken ct)`

### Infrastructure layer — data models

- [X] T008 [P] Add `public Guid? ReviewerId { get; set; }` to `src/MeisterProPR.Infrastructure/Data/Models/ClientRecord.cs`
- [X] T009 [P] Remove `public Guid ReviewerId { get; set; }` from `src/MeisterProPR.Infrastructure/Data/Models/CrawlConfigurationRecord.cs`
- [X] T010 Add `reviewer_id` column mapping to `src/MeisterProPR.Infrastructure/Data/Configurations/ClientEntityTypeConfiguration.cs` (nullable `uuid`, no default)
- [X] T011 Update `src/MeisterProPR.Infrastructure/Data/Configurations/CrawlConfigurationEntityTypeConfiguration.cs`: remove `reviewer_id` from unique index; recreate index on `(client_id, organization_url, project_id)` only

### Infrastructure layer — repositories

- [X] T012 Implement `GetReviewerIdAsync` in `src/MeisterProPR.Infrastructure/Repositories/PostgresClientRegistry.cs` (query `clients` table for `reviewer_id` by `clientId`)
- [X] T013 Implement `GetReviewerIdAsync` in `src/MeisterProPR.Infrastructure/Configuration/EnvVarClientRegistry.cs` (always return `null` — no DB in env-var mode)
- [X] T014 Update `src/MeisterProPR.Infrastructure/Repositories/PostgresCrawlConfigurationRepository.cs`: remove `reviewerId` from `AddAsync` and `ExistsAsync`; update all `Select` projections to join `c.Client.ReviewerId` (or `LEFT JOIN clients`) to populate `CrawlConfigurationDto.ReviewerId` from the client record
- [X] T015 Update `src/MeisterProPR.Infrastructure/AzureDevOps/AdoAssignedPrFetcher.cs`: `config.ReviewerId` is now `Guid?`; if null, skip this config (log warning, return empty list)

### EF Core migration

- [X] T016 Generate EF Core migration `AddReviewerIdToClients_RemoveFromCrawlConfigs` using `dotnet ef migrations add`; verify `Up()` adds `reviewer_id uuid NULL` to `clients` and drops `reviewer_id` from `crawl_configurations` and recreates unique index

**Checkpoint**: All projects compile; T001–T003 tests still fail (no production logic yet); all other foundational tests pass.

---

## Phase 3: User Story 3 — Configure AI Reviewer Identity (Priority: P2)

**Goal**: Admin can set and read the AI reviewer GUID on a client via the management API.

**Independent Test**: `PUT /clients/{id}/reviewer-identity` stores the GUID; subsequent
`GET /clients/{id}` returns it in `reviewerId`; crawl config creation no longer accepts
or requires `reviewerDisplayName`.

**Why before US1**: US1's orchestration reads `ReviewerId` from the client record — this
endpoint is the only way to populate it.

### Tests (write first — confirm failing)

- [X] T017 [TEST] [P] [US3] Write failing integration test: `PUT /clients/{id}/reviewer-identity` with valid GUID returns `204` and GUID is persisted in `tests/MeisterProPR.Api.Tests/Controllers/ClientsControllerReviewerTests.cs`
- [X] T018 [TEST] [P] [US3] Write failing integration test: `GET /clients/{id}` response includes `reviewerId` field (non-null after set, null before) in `tests/MeisterProPR.Api.Tests/Controllers/ClientsControllerReviewerTests.cs`
- [X] T019 [TEST] [P] [US3] Write failing integration test: `PUT /clients/{id}/reviewer-identity` with `Guid.Empty` returns `400` in `tests/MeisterProPR.Api.Tests/Controllers/ClientsControllerReviewerTests.cs`
- [X] T020 [TEST] [P] [US3] Write failing integration test: `POST /clients/{id}/crawl-configs` without `reviewerDisplayName` field succeeds in `tests/MeisterProPR.Api.Tests/Controllers/ClientsControllerCrawlConfigTests.cs`
- [X] T032 [TEST] [P] [US3] Write failing unit test: `ReviewOrchestrationService.ProcessAsync` calls `SetFailed` with a message containing "not configured" when `GetReviewerIdAsync` returns `null`, and does NOT call `AddOptionalReviewerAsync` or `PostAsync` in `tests/MeisterProPR.Application.Tests/Services/ReviewOrchestrationServiceTests.cs`
- [X] T033 [TEST] [P] [US3] Write failing unit test: when `job.ClientId` is `null`, `ProcessAsync` calls `SetFailed` immediately and does NOT call `GetReviewerIdAsync` in `tests/MeisterProPR.Application.Tests/Services/ReviewOrchestrationServiceTests.cs`

> **⚠️ TDD gate**: T032 and T033 MUST be red before T030 (Phase 4) begins — T030 calls `job.ClientId.Value` and the null guards that T036 implements must be in place first.

### Implementation

- [X] T021 [US3] Update `ClientResponse` record in `src/MeisterProPR.Api/Controllers/ClientsController.cs` to include `Guid? ReviewerId`; update `ToClientResponse()` helper to map `client.ReviewerId`
- [X] T022 [US3] Remove `Guid ReviewerId` from `CrawlConfigResponse` record and remove `ReviewerDisplayName` from `CreateCrawlConfigRequest` record in `src/MeisterProPR.Api/Controllers/ClientsController.cs`; remove identity-resolver call from `PostCrawlConfig` action; update both `ICrawlConfigurationRepository.AddAsync` call (no `reviewerId` arg) and `ICrawlConfigurationRepository.ExistsAsync` call (new 3-parameter signature)
- [X] T023 [US3] Add `PutReviewerIdentity` action to `src/MeisterProPR.Api/Controllers/ClientsController.cs`: `[HttpPut("clients/{clientId:guid}/reviewer-identity")]`, body `SetReviewerIdentityRequest(Guid ReviewerId)`, admin-key guard, validate `ReviewerId != Guid.Empty`, find client (404 if missing), set `client.ReviewerId`, `SaveChangesAsync`, return `204 No Content` with full XML docs
- [X] T036 [US3] Update `src/MeisterProPR.Application/Services/ReviewOrchestrationService.cs`: add `IAdoReviewerManager reviewerManager` and `IClientRegistry clientRegistry` constructor parameters; add guards at top of `ProcessAsync` — if `job.ClientId is null` → `jobs.SetFailed(job.Id, "No client associated with job")` + return; call `GetReviewerIdAsync(job.ClientId.Value, ct)`; if result is `null` → `jobs.SetFailed(job.Id, $"Reviewer identity not configured for client {job.ClientId}")` + log structured warning + return
- [X] T024 [US3] Regenerate `openapi.json` at repository root: `dotnet build` + Swashbuckle CLI or run app and export; commit updated file

**Checkpoint**: US3 independently testable — T017–T020, T032, T033 green; admin can configure reviewer identity; null-guard logic in place before Phase 4 begins.

---

## Phase 4: User Story 1 — AI Reviewer Visible on Pull Request (Priority: P1) 🎯 MVP

**Goal**: When a review job runs, the AI identity is added as an optional reviewer on the
PR before any comments are posted. Applies to both manual and crawl-based jobs.

**Independent Test**: Submit a review job for a client with `ReviewerId` configured → job
completes → AI service account appears as optional reviewer on the PR in ADO.

### Tests (write first — confirm failing)

- [X] T025 [TEST] [P] [US1] Write failing unit test: `ReviewOrchestrationService.ProcessAsync` calls `IAdoReviewerManager.AddOptionalReviewerAsync` with the client's `ReviewerId` before `IAdoCommentPoster.PostAsync` in `tests/MeisterProPR.Application.Tests/Services/ReviewOrchestrationServiceTests.cs`
- [X] T026 [TEST] [P] [US1] Write failing unit test: when `IClientRegistry.GetReviewerIdAsync` returns non-null, `AddOptionalReviewerAsync` is called with that GUID in `tests/MeisterProPR.Application.Tests/Services/ReviewOrchestrationServiceTests.cs`
- [X] T027 [TEST] [P] [US1] Write failing unit test: `AdoReviewerManager.AddOptionalReviewerAsync` calls `GitHttpClient.CreatePullRequestReviewerAsync` with `Vote=0` and `IsRequired=false` in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoReviewerManagerTests.cs`
- [X] T028 [TEST] [P] [US1] Write failing unit test: idempotency — calling `AddOptionalReviewerAsync` twice does not throw (ADO PUT is idempotent) in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoReviewerManagerTests.cs`

### Implementation

- [X] T029 [US1] Create `src/MeisterProPR.Infrastructure/AzureDevOps/AdoReviewerManager.cs` implementing `IAdoReviewerManager`: resolve credentials → `GetConnectionAsync` → `GetClient<GitHttpClient>` → `CreatePullRequestReviewerAsync(new GitPullRequestReviewer { Vote = 0, IsRequired = false, Id = reviewerId.ToString() }, repositoryId, pullRequestId, reviewerId.ToString(), projectId, ct)`; wrap in `ActivitySource` span with `pr.id` and `reviewer.id` attributes; log success with `[LoggerMessage]`
- [X] T030 [US1] Extend `src/MeisterProPR.Application/Services/ReviewOrchestrationService.cs` (null guards already added by T036): after the null guards, call `AddOptionalReviewerAsync(job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, reviewerId.Value, job.ClientId, ct)` before `aiCore.ReviewAsync`; existing outer catch propagates ADO failures to `SetFailed`
- [X] T031 [US1] Register `AdoReviewerManager` as `IAdoReviewerManager` singleton in `src/MeisterProPR.Infrastructure/DependencyInjection/InfrastructureServiceExtensions.cs`

**Checkpoint**: US1 independently testable — T025–T028 green; end-to-end: job completes and AI appears as reviewer on PR.

---

## Phase 5: User Story 2 — Job Failure When ADO Reviewer Addition Fails (Priority: P2)

**Goal**: When the ADO reviewer addition call fails at runtime (permission denied, closed PR,
network error), the job is marked failed and no comments are posted.

> **Note**: The null-`ReviewerId` rejection path (US3 Acceptance Scenario 3) is implemented
> in Phase 3 (T032/T033/T036) so it is in place before Phase 4 begins.

**Independent Test**: Simulate ADO permission denied on reviewer addition → job transitions
to `failed`, `PostAsync` is never called, failure reason recorded in job log.

### Tests (write first — confirm failing)

- [X] T034 [TEST] [P] [US2] Write failing unit test: when `AddOptionalReviewerAsync` throws, `PostAsync` is NOT called and job transitions to `failed` in `tests/MeisterProPR.Application.Tests/Services/ReviewOrchestrationServiceTests.cs`
- [X] T035 [TEST] [P] [US2] Write failing unit test: crawl worker skips configs where `config.ReviewerId` is `null` and logs a structured warning in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoAssignedPrFetcherTests.cs`

### Implementation

- [X] T037 [US2] Update `src/MeisterProPR.Infrastructure/AzureDevOps/AdoAssignedPrFetcher.cs`: when `config.ReviewerId is null`, log structured warning with `[LoggerMessage]` (`"Skipping crawl config {ConfigId} for client {ClientId} — reviewer identity not configured"`) and return empty list (depends on T015)

**Checkpoint**: US2 independently testable — T034/T035 green; ADO failures fail the job without posting comments; crawl configs with null reviewer are skipped with a warning.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [X] T038 [P] Verify all new `[LoggerMessage]` source-generated log statements have correct event IDs and log levels in `AdoReviewerManager.cs` and `ReviewOrchestrationService.cs`
- [X] T039 [P] Run `dotnet test` — confirm all tests green (target: all existing tests still pass + all new tests pass)
- [X] T040 [P] Run `dotnet build` and verify no compiler warnings on changed files
- [X] T041 Validate `openapi.json` is up to date (run app locally or use Swashbuckle CLI); confirm `reviewerId` appears on `ClientResponse` and is absent from `CrawlConfigResponse`
- [X] T042 Follow quickstart.md migration steps against a local Postgres instance to confirm the migration applies cleanly
- [X] T043 Regenerate `admin-ui/src/services/generated/openapi.ts` from updated `openapi.json` using `npm run generate:api` in `admin-ui/`
- [X] T044 Update `admin-ui/src/views/ClientDetailView.vue`: replace GUID input with display-name resolution form — org URL + display name inputs, "Resolve" button calling `GET /identities/resolve`, identity pick list, save wired to `PUT /clients/{clientId}/reviewer-identity`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 2)**: No prior phases — start immediately; blocks all user stories
- **US3 (Phase 3)**: Depends on Phase 2 complete — provides null-guard tests T032/T033 and implementation T036 that Phase 4 requires to be in place first
- **US1 (Phase 4)**: Depends on Phase 3 complete — T030 extends the method that T036 already established; null guards must exist before T030 calls `.Value`
- **US2 (Phase 5)**: Depends on Phase 4 complete — tests ADO-failure path through the complete orchestration sequence
- **Polish (Phase 6)**: Depends on all story phases complete

### User Story Dependencies

- **US3 (P2 in spec, Phase 3 in impl)**: First user story phase — provides both the admin API and the null-guard safety net that US1 depends on
- **US1 (P1 in spec, Phase 4 in impl)**: Depends on US3 (needs ReviewerId populated AND null guards in place)
- **US2 (P2 in spec, Phase 5 in impl)**: Depends on US1 — tests the failure path through the complete orchestration

### Within Each Phase: [TEST] tasks MUST run first

```
[TEST] tasks → confirm FAILING → implement production code → confirm PASSING
```

### Parallel Opportunities

Within Phase 2: T001/T002/T003 (test writing) can run in parallel; T004/T005/T006 (interface/DTO changes) can run in parallel; T008/T009 (model changes) can run in parallel.

Within Phase 3: T017/T018/T019/T020/T032/T033 (test writing) can run in parallel.

Within Phase 4: T025/T026/T027/T028 (test writing) can run in parallel.

Within Phase 5: T034/T035 (test writing) can run in parallel.

---

## Parallel Example: Phase 2 Foundational

```
# Run all test-writing tasks together first (all [P]):
T001 — ClientRegistry.GetReviewerIdAsync null-return test
T002 — EnvVarClientRegistry.GetReviewerIdAsync null-return test
T003 — CrawlConfigurationDto.ReviewerId nullable test

# Then, in parallel, update models and interfaces (all [P]):
T004 — IClientRegistry new method
T005 — IAdoReviewerManager new interface
T006 — CrawlConfigurationDto ReviewerId nullable
T008 — ClientRecord add ReviewerId
T009 — CrawlConfigurationRecord remove ReviewerId
```

---

## Implementation Strategy

### MVP First (Phase 2 + Phase 3 + Phase 4)

1. Complete Phase 2: Foundational (schema + contracts)
2. Complete Phase 3: US3 (admin can set ReviewerId on client)
3. Complete Phase 4: US1 (reviewer added to PR on job run)
4. **STOP and VALIDATE**: end-to-end test — configure client → run job → verify PR reviewer
5. Deploy if ready

### Incremental Delivery

1. Phase 2 → project compiles with updated schema/contracts
2. Phase 3 → admin API works; existing jobs still fail cleanly (null ReviewerId guard in Phase 5)
3. Phase 4 → reviewer addition live for configured clients
4. Phase 5 → null/failure edge cases fully covered
5. Phase 6 → all tests green, openapi.json committed

---

## Notes

- T036 (Phase 3) and T030 (Phase 4) both modify `ReviewOrchestrationService.cs` — T036 adds null guards first; T030 then adds the reviewer call on top
- T022 modifies `ClientsController.cs` (removes reviewer from crawl config) and T023 adds the new endpoint to the same file — complete T021 before T022 before T023
- T014 and T015 both modify crawl-related infrastructure — T014 (repo) before T015 (fetcher) to keep the DTO shape consistent
- T015 and T037 both modify `AdoAssignedPrFetcher.cs` — T015 (Phase 2) adds the null-skip logic first; T037 (Phase 5) adds structured warning logging on top of T015
- `EnvVarClientRegistry.GetReviewerIdAsync` always returns `null` — in non-DB mode, reviewer addition is never triggered (no `ClientId` on manually created jobs in that mode)
- Confirm each `[TEST]` task is **red** (failing) before writing the corresponding implementation task
