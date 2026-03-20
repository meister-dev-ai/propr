# Tasks: React to Mentions in PR Comments

**Feature Branch**: `009-react-to-mentions`  
**Input**: [plan.md](plan.md), [spec.md](spec.md), [data-model.md](data-model.md), [contracts/internal-contracts.md](contracts/internal-contracts.md), [research.md](research.md)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Domain enums, value objects, and interfaces that all components depend on.

- [X] T001 [P] Create `MentionJobStatus` enum in `src/MeisterProPR.Domain/Enums/MentionJobStatus.cs`
- [X] T002 [P] Create `ActivePullRequestRef` value object in `src/MeisterProPR.Domain/ValueObjects/ActivePullRequestRef.cs`
- [X] T003 Create `MentionReplyJob` entity with state machine in `src/MeisterProPR.Domain/Entities/MentionReplyJob.cs`
- [X] T004 [P] Create `MentionProjectScan` entity in `src/MeisterProPR.Domain/Entities/MentionProjectScan.cs`
- [X] T005 [P] Create `MentionPrScan` entity in `src/MeisterProPR.Domain/Entities/MentionPrScan.cs`
- [X] T006 [P] Create `IMentionAnswerService` Domain interface in `src/MeisterProPR.Domain/Interfaces/IMentionAnswerService.cs`
- [X] T007 [P] Create `IActivePrFetcher` Application interface in `src/MeisterProPR.Application/Interfaces/IActivePrFetcher.cs`
- [X] T008 [P] Create `IAdoThreadReplier` Application interface in `src/MeisterProPR.Application/Interfaces/IAdoThreadReplier.cs`
- [X] T009 [P] Create `IMentionScanRepository` Application interface in `src/MeisterProPR.Application/Interfaces/IMentionScanRepository.cs`
- [X] T010 [P] Create `IMentionReplyJobRepository` Application interface in `src/MeisterProPR.Application/Interfaces/IMentionReplyJobRepository.cs`

---

## Phase 2: Foundational (Storage & DI)

**Purpose**: EF Core mapping, migration, and DI wiring. Required for integration tests.

- [X] T011 Add EF entity type configurations: `MentionReplyJobConfiguration`, `MentionProjectScanConfiguration`, `MentionPrScanConfiguration` in `src/MeisterProPR.Infrastructure/Data/Configurations/`
- [X] T012 Add three new `DbSet<>` properties to `src/MeisterProPR.Infrastructure/Data/MeisterProPRDbContext.cs`
- [X] T013 Generate EF Core migration `AddMentionScanTables` in `src/MeisterProPR.Infrastructure/Migrations/`
- [X] T014 [P] Create `StubActivePrFetcher` and `StubAdoThreadReplier` in `src/MeisterProPR.Infrastructure/AzureDevOps/`
- [X] T015 Register `Channel<MentionReplyJob>` and workers in `src/MeisterProPR.Api/Program.cs`
- [X] T016 Register repositories and services in `src/MeisterProPR.Infrastructure/DependencyInjection/InfrastructureServiceExtensions.cs`

---

## Phase 3: [TEST] Red Phase (Verification)

**Purpose**: Mandatory failing tests before implementation begins (Constitution Principle II).

- [X] T017 [P] [TEST] Unit tests for `MentionDetector`: GUID match and display-name fallback in `tests/MeisterProPR.Application.Tests/Services/MentionDetectorTests.cs`
- [X] T018 [P] [TEST] Unit tests for `MentionScanService`: skip logic, detection, and watermark updates in `tests/MeisterProPR.Application.Tests/Services/MentionScanServiceTests.cs`
- [X] T019 [P] [TEST] Unit tests for `MentionReplyService`: happy path and failure modes in `tests/MeisterProPR.Application.Tests/Services/MentionReplyServiceTests.cs`
- [X] T020 [P] [TEST] Integration tests for `EfMentionReplyJobRepository`: state machine and deduplication in `tests/MeisterProPR.Infrastructure.Tests/Repositories/EfMentionReplyJobRepositoryTests.cs`
- [X] T021 [P] [TEST] Integration tests for `EfMentionScanRepository`: watermark persistence in `tests/MeisterProPR.Infrastructure.Tests/Repositories/EfMentionScanRepositoryTests.cs`
- [ ] T022 [TEST] `WebApplicationFactory` integration test: end-to-end Channel hydration and processing in `tests/MeisterProPR.Api.Tests/Workers/MentionWorkerIntegrationTests.cs`
- [ ] T023 [TEST] Efficiency verification (SC-003): Confirm ADO call counts remain low for idle PRs via mock/stub.

---

## Phase 4: User Story 1 — Detection & Enqueue (Priority: P1)

- [X] T024 [P] [US1] Implement `MentionDetector.IsMentioned` (GUID-first, display-name fallback)
- [X] T025 [P] [US1] Implement `EfMentionReplyJobRepository` and `EfMentionScanRepository`
- [X] T026 [P] [US1] Implement `AdoActivePrFetcher.GetRecentlyUpdatedPullRequestsAsync`
- [X] T027 [US1] Implement `MentionScanService.ScanAsync` (full discovery/enqueue loop)
- [X] T028 [US1] Implement `MentionScanWorker` (producer loop using `PeriodicTimer`)

---

## Phase 5: User Story 2 — Context & Reply (Priority: P2)

- [X] T029 [P] [US2] Implement `AdoThreadReplier.ReplyAsync`
- [X] T030 [P] [US2] Implement `AgentMentionAnswerService.AnswerAsync` (AI grounded in PR context)
- [X] T031 [US2] Implement `MentionReplyService.ProcessAsync` (orchestration)
- [X] T032 [US2] Implement `MentionReplyWorker` (Channel consumer loop + startup hydration)

---

## Phase 6: User Story 3 — Efficiency & Observability (Priority: P3)

- [X] T033 [US3] Add structured logging for skip decisions and cycle metrics in `MentionScanService`
- [X] T034 [US3] Add OTel `ActivitySource` spans around ADO and AI operations
- [X] T035 [US3] Implement `MENTION_CRAWL_INTERVAL_SECONDS` enforcement (min 10s clamp)

---

## Phase 7: Final Validation & Polish

- [ ] T036 [TEST] Performance check (SC-005): Verify mention scanning doesn't degrade review job throughput by >5% via baseline comparison.
- [X] T037 [P] Verify `ADO_STUB_PR=true` wiring in `Program.cs`
- [X] T038 Update `docs/getting-started.md` with new configuration and metrics
- [X] T039 Final `dotnet test` and `dotnet build` verification
