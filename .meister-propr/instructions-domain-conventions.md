"""
description: Provider-neutral review domain conventions for ReviewJob, PullRequest, carry-forward behavior, and AI connection entities.
when-to-use: When files change in src/MeisterProPR.Domain/, review orchestration, review persistence, provider-neutral review models, or related tests.
"""

# Domain Conventions

## ReviewJob

- `ReviewJob` transitions through `Pending -> Processing -> (Completed | Failed | Cancelled)`.
- Startup recovery intentionally moves stale `Processing` jobs back to `Pending`.
- `jobs.DeleteAsync(job.Id)` after an empty/no-op review is intentional. The current reviewing flow saves scan state first and then removes the job instead of leaving a useless terminal record.
- The constructor seeds Azure DevOps-compatible legacy fields by default. That is backward compatibility, not proof that the review system is Azure DevOps-only.
- Modern cross-provider context is added through intentional mutator methods such as:
  - `SetProviderReviewContext(CodeReviewRef)`
  - `SetReviewRevision(ReviewRevision?)`
  - `SetPrContext(...)`
  - `SetAiConfig(...)`
  - `SetProCursorSourceScope(...)`
- Unless directed otherwise this should be kept with broad public setters; they preserve invariants while allowing the intake path to enrich the job after creation.

## Provider-Neutral Review References

- Cross-provider behavior should be expressed through normalized references such as `ProviderHostRef`, `RepositoryRef`, `CodeReviewRef`, `ReviewRevision`, `ReviewThreadRef`, and `ReviewCommentRef`.
- When working on provider-neutral code, prefer `job.CodeReviewReference`, `job.RepositoryReference`, and `job.ReviewRevisionReference` over reasoning only from legacy Azure DevOps field names.
- Reviewer-trigger identity is configuration state only. It is not the authenticated publication identity and should not be persisted or interpreted as one.

## PullRequest Value Object

- `PullRequest` carries more than the original minimal PR identity. In addition to repository/PR identity and `ChangedFiles`, current executions may include:
  - `Status`
  - `ExistingThreads`
  - `AllChangedFileSummaries`
  - `AuthorizedIdentityId`
  - `AuthorizedIdentityName`
- On incremental reviews, `ChangedFiles` is the delta since the last reviewed iteration, not the full PR file set.
- `AllPrFileSummaries` is the full PR manifest and is intentionally derived from `AllChangedFileSummaries` when present.
- `ExistingThreads` and authorized identity metadata are load-bearing for duplicate suppression, reviewer-owned thread detection, and summary publication behavior.
- In tests, `RepositoryId` and `RepositoryName` are often the same placeholder value. That is fine.

## AiConnection

- `AiConnection` is a per-client AI endpoint record. Exactly one connection per client may be active at a time.
- `ApiKey` is optional. `null` means the runtime falls back to the ambient Azure credential chain rather than a stored API key.
- `Models` is the allowed deployment/model set for that endpoint, and `Activate(...)` intentionally enforces that `ActiveModel` is a member of `Models`.
- `ModelCategory` is intentional. It lets the runtime choose tier-specific AI connections for different file-complexity tiers.
- Repository activation currently deactivates other connections before activating the requested one. That two-step repository behavior is the current design.

## ReviewFileResult And Carry-Forward

- One `ReviewFileResult` row still represents one `(job, file)` outcome.
- `IsExcluded` means the file matched exclusion rules and skipped AI review.
- `IsCarriedForward` means the run reused a prior reviewed result without a new AI call.
- Carried-forward rows remain part of stored review history, but they are intentionally excluded from fresh synthesis input and from new outbound SCM comment posting.

## Comment Resolution

- `CommentResolutionBehavior` is currently `Disabled`, `Silent`, or `WithReply`.
- `WithReply` requires an explanation before resolving an AI-owned thread.
- `Silent` skips the reply but still allows provider-native resolution.
- These behaviors apply to automated reviewer-owned thread handling, not to arbitrary human-owned threads.
