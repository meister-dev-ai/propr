# Tasks: Resolve PR Comments

**Input**: Design documents from `/specs/011-resolve-pr-comments/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

## Phase 1: Setup & Foundational

**Purpose**: Database schema changes and core entity definitions that all stories depend on.
**Note**: We combine Setup and Foundational here because the existing application is already structured and running. The only setup is database and entity extensions.

- [ ] T001 [P] Create `CommentResolutionBehavior` enum in `src/MeisterProPR.Domain/Enums/CommentResolutionBehavior.cs`
- [ ] T002 [P] Create `ReviewPrScan` entity in `src/MeisterProPR.Domain/Entities/ReviewPrScan.cs`
- [ ] T003 Update `ClientRecord` in `src/MeisterProPR.Infrastructure/Data/Models/ClientRecord.cs` to include `CommentResolutionBehavior`
- [ ] T004 Add `DbSet<ReviewPrScan> ReviewPrScans` to `src/MeisterProPR.Infrastructure/Data/MeisterProPRDbContext.cs` and configure entity mappings if needed
- [ ] T005 Create EF Core migration `AddResolvePrComments` via `dotnet ef migrations add AddResolvePrComments` in `src/MeisterProPR.Infrastructure`

---

## Phase 2: User Story 2 - Efficient Re-evaluation on New Commits Only (Priority: P2)

**Goal**: Track the latest commit identifier processed for a PR so the system only evaluates unresolved comments when new commits are detected. Note: We tackle P2 first because P1 depends on this mechanism to know *when* to evaluate.

**Independent Test**: Can be tested by observing the system's processing logs when checking a pull request with unresolved comments but no new commits (should be zero re-evaluation processing).

### Tests for User Story 2

- [ ] T006 [P] [TEST] [US2] Add unit tests for `ReviewPrScan` entity instantiation and validation in `tests/MeisterProPR.Domain.Tests/Entities/ReviewPrScanTests.cs`
- [ ] T007 [P] [TEST] [US2] Add integration test to verify `ReviewPrScan` persistence in `tests/MeisterProPR.Infrastructure.Tests/Data/ReviewPrScanRepositoryTests.cs` (or equivalent)
- [ ] T027 [P] [TEST] [US2] Add unit tests for `AdoPrCrawlerWorker` logic verifying thread filtering and `ReviewPrScan` checks in `tests/MeisterProPR.Api.Tests/Workers/AdoPrCrawlerWorkerTests.cs`

### Implementation for User Story 2

- [ ] T008 [P] [US2] Implement `IReviewPrScanRepository` interface in `src/MeisterProPR.Application/Interfaces/IReviewPrScanRepository.cs`
- [ ] T009 [US2] Implement repository in `src/MeisterProPR.Infrastructure/Repositories/ReviewPrScanRepository.cs` (and register in DI)
- [ ] T010 [US2] Update crawler worker (`AdoPrCrawlerWorker.cs` or equivalent) to fetch `ReviewPrScan` by PR. Skip code-change re-evaluation if the PR's latest commit ID matches `LastProcessedCommitId`. Per FR-005, still evaluate any reviewer thread where the current reply count exceeds `LastSeenReplyCount` stored for that thread.
- [ ] T011 [US2] Update crawler worker to save/update `ReviewPrScan.LastProcessedCommitId` and per-thread `LastSeenReplyCount` after each processing pass.
- [ ] T026 [US2] Extend `ReviewPrScan` (or add a `ReviewPrScanThread` child entity in `src/MeisterProPR.Domain/Entities/ReviewPrScanThread.cs`) to store per-thread state: `ThreadId` (int) and `LastSeenReplyCount` (int). Update `data-model.md` and create a new EF Core migration `AddReviewPrScanThread`.

---

## Phase 3: User Story 1 - Automatic Resolution of Fixed Issues (Priority: P1) 🎯 MVP

**Goal**: Automatically resolve comments when a pushed fix addresses the issue. Includes client configuration and AI evaluation.

**Independent Test**: Can be fully tested by creating a pull request, letting the automated reviewer comment, pushing a fix for the comment, and verifying the system changes the comment status to resolved.

### Tests for User Story 1

- [ ] T012 [P] [TEST] [US1] Add unit tests for `CommentResolutionBehavior` parsing/handling in DTOs.
- [ ] T013 [P] [TEST] [US1] Add unit/integration tests for resolving comments via `IAdoThreadClient` (mocking ADO API) in `tests/MeisterProPR.Infrastructure.Tests/AzureDevOps/AdoThreadClientTests.cs`
- [ ] T014 [TEST] [US1] Add unit tests verifying AI prompt logic for comment resolution in `tests/MeisterProPR.Infrastructure.Tests/AI/AiReviewerTests.cs` (or equivalent)

### Implementation for User Story 1 (API & Client Configuration)

- [ ] T015 [P] [US1] Update `ClientDto` in `src/MeisterProPR.Application/DTOs/ClientDto.cs` to include `CommentResolutionBehavior`
- [ ] T016 [P] [US1] Update `CreateClientRequest`, `PatchClientRequest`, and `ClientResponse` in `src/MeisterProPR.Api/Controllers/ClientsController.cs` to handle the behavior field.
- [ ] T017 [US1] Update `ClientsController` to map the new field when creating/updating clients and ensure tests in `tests/MeisterProPR.Api.Tests/Controllers/ClientsControllerTests.cs` pass.
- [ ] T018 [US1] Update Admin UI: Add `CommentResolutionBehavior` select/radio input in `admin-ui/src/views/ClientEdit.vue` and `admin-ui/src/components/ClientForm.vue` (and corresponding types).

### Implementation for User Story 1 (ADO & AI Integration)

- [ ] T019 [P] [US1] Add `UpdateThreadStatusAsync(string organizationUrl, Guid projectId, Guid repositoryId, int pullRequestId, int threadId, string status)` to `IAdoThreadClient` in `src/MeisterProPR.Application/Interfaces/IAdoThreadClient.cs` and implement it in `src/MeisterProPR.Infrastructure/AzureDevOps/AdoThreadClient.cs` using the ADO PATCH thread endpoint; set status to `"fixed"`.
- [ ] T020 [US1] Update AI evaluation logic (e.g., in `AiReviewService.cs` or equivalent). Filter PR threads to only those created by the reviewer's own identity. Implement two distinct AI prompt paths: (1) If `LastProcessedCommitId` changed, check if the new diff resolves the comment context. (2) If no new commits but new replies exist, generate a conversational response to the user's question/statement based on the thread history.
- [ ] T021 [US1] Implement handling of AI response: if fixed, call ADO client to update thread status based on client's `CommentResolutionBehavior`. If behavior is `WithReply`, ensure the AI's generated reasoning is posted as a new comment before resolving. If conversational (path 2), post the AI's response as a new comment in the thread.
- [ ] T022 [US1] Implement edge case handling: Ensure AI explicitly returns "unresolved" if unsure.

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T023 [P] Update OpenAPI specifications (ensure Swashbuckle generates correct enum strings for `CommentResolutionBehavior`).
- [ ] T024 [P] Update `openapi.json` at repo root (`npm run generate:api` in admin-ui if applicable, or via Swashbuckle CLI).
- [ ] T025 Ensure logging does not leak ADO tokens during thread resolution.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: Must be completed first to establish database schema and base domain types.
- **User Story 2 (Phase 2)**: Depends on Setup. Must be done before US1 because the commit tracking and per-thread reply tracking (T026) are prerequisites for triggering the re-evaluation logic cleanly.
- **User Story 1 (Phase 3)**: Depends on US2 (for knowing *when* to evaluate and which threads have new replies).

### Parallel Opportunities

- T001 and T002 can run in parallel.
- Tests (T006, T007) can run in parallel with implementation interfaces (T008).
- DTO/Controller updates (T015, T016) can run in parallel with ADO Client updates (T019).
- Frontend Admin UI updates (T018) can run in parallel with backend AI integration (T020).