"""
description: Domain entity conventions — ReviewJob state machine, PullRequest value object, AiConnection entity, and review result types.
when-to-use: When files change in src/MeisterProPR.Domain/, src/MeisterProPR.Application/Services/, or src/MeisterProPR.Infrastructure/Repositories/.
"""

# Domain Conventions

## ReviewJob State Machine

`ReviewJob` transitions through: `Pending → Processing → (Completed | Failed | Cancelled)`. Transitions are guarded in `TryTransitionAsync` using optimistic concurrency (EF Core `RowVersion`). Catching `DbUpdateConcurrencyException` and returning `false` is the correct pattern — the caller decides whether to retry.

`jobs.DeleteAsync` is called on successful no-op reviews (no change detected, no new thread replies). This is intentional — the job served its purpose and is cleaned up rather than left in a terminal state. It is not a premature deletion.

## PullRequest Value Object

`PullRequest` is a record with these positional parameters in order:
1. `OrganizationUrl` (string)
2. `ProjectId` (string)
3. `RepositoryId` (string)
4. `RepositoryName` (string) ← display name from ADO, distinct from RepositoryId
5. `PullRequestId` (int)
6. `IterationId` (int)
7. `Title` (string)
8. `Description` (string?)
9. `SourceBranch` (string)
10. `TargetBranch` (string)
11. `ChangedFiles` (IReadOnlyList<ChangedFile>)

In tests, `RepositoryId` and `RepositoryName` are often both set to the same placeholder value (e.g., `"repo"`). This is correct — they are logically distinct but use the same value in test fixtures for simplicity.

## AiConnection Entity

`AiConnection` represents a per-client AI endpoint configuration. Key invariants:
- Only one `AiConnection` per client can be `IsActive = true` at a time (enforced by a filtered unique index on `(ClientId)` where `is_active = true`).
- `ApiKey` is write-only — it is stored but never surfaced in DTOs or GET responses.
- `ApiKey` is write-only — it is stored protected using the project's protection codec (purpose `AiConnectionApiKey`) and never surfaced in DTOs or GET responses.
- Client ADO credentials (PATs) are persisted protected using the `ClientAdoCredentials` purpose; repositories should Protect on write and Unprotect on read where appropriate.
- `Models` is a list of deployment/model names the endpoint supports. `ActiveModel` must be one of the values in `Models`.
- `ActivateAsync` in the repository deactivates all other connections before activating the target. The two-step update is acceptable for the current concurrency requirements.

## ReviewFileResult

One `ReviewFileResult` row is created per (job, file) pair. States: incomplete → (complete | failed | excluded | carried-forward). `IsCarriedForward` means the result was inherited from a prior iteration's job without a new AI call. `IsExcluded` means the file matched an exclusion pattern.

## Carry-Forward Logic

When a PR has a new iteration, only files that changed since the last reviewed iteration get new AI calls. Files that did NOT change are carried forward from the prior iteration's completed `ReviewFileResult` rows. The final posted review covers all PR files (delta + carried-forward).
