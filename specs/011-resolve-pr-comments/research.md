# Research: Resolve PR Comments

## 1. Tracking Reviewed State

- **Decision**: Create a `ReviewPrScan` entity and corresponding `ReviewPrScanRecord` in EF Core.
- **Rationale**: The system MUST NOT re-evaluate comments or trigger processing if no new commits have been added (FR-005) and MUST track the latest commit identifier (FR-001). A dedicated entity (mirroring the existing `MentionPrScan` pattern) allows persisting the `LastProcessedCommitId` per PR per Client/CrawlConfig, ensuring we do not repeatedly process the same commits.
- **Alternatives considered**: Adding state to the existing `ReviewJob` entity. However, `ReviewJob` represents a single execution of a review, not the ongoing state of the PR itself across multiple review jobs. Using a dedicated scan record is much cleaner and aligns with existing paradigms.

## 2. Resolving Comments via ADO API

- **Decision**: Add `UpdateThreadStatusAsync` to `IAdoThreadClient` (the general PR-reviewer interface, distinct from `IAdoCommentPoster` which only reacts to mentions). `IAdoThreadClient` handles actions taken by the reviewer as a first-class participant on any PR, not just when mentioned.
- **Rationale**: ADO REST API supports updating pull request threads (`PATCH https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads/{threadId}?api-version=7.1`). The canonical resolved status to set is `fixed` (FR-004). `IAdoCommentPoster` is scoped to mention-driven reactions and is the wrong abstraction for proactive reviewer actions.
- **Alternatives considered**: Extending `IAdoPullRequestClient` — rejected because that interface is about PR-level data retrieval, not thread mutation. Extending `IAdoCommentPoster` — rejected because it conflates mention-reactive posting with general reviewer actions.

## 3. Detecting Fixes using the AI Reviewer

- **Decision**: Add an AI prompt step that provides the unresolved comment context alongside the new code changes to determine if the comment was addressed.
- **Rationale**: The AI must determine if the "new changes address the issue in an unresolved comment" (FR-004).
- **Alternatives considered**: Doing simple regex matching or heuristic checks, which would be highly unreliable given natural language comments.

## 4. Configurable Client Behavior

- **Decision**: Introduce a `CommentResolutionBehavior` enum and add it to `ClientRecord` and `ClientDto`.
- **Rationale**: The requirement states "configurable behavior on a per-client basis for comment resolution, defaulting to silently changing the thread status without adding a reply" (FR-006). This enum will have values: `Disabled`, `Silent`, and `WithReply` (with `Silent` as default).
- **Alternatives considered**: A boolean `ResolveCommentsSilently`. An enum is more extensible if we want to add more behaviors later. Config on `CrawlConfiguration` was considered, but the spec explicitly stated "per-client basis".

## 5. Handling Re-opened Comments

- **Decision**: Treat manually re-opened comments as active unresolved comments.
- **Rationale**: When fetching threads via the ADO API, threads that are marked as active/unresolved should be evaluated, regardless of their past history. As long as they are active and there are new commits since the `LastProcessedCommitId`, the AI will process them.

## 6. Continuous Evaluation & AI Uncertainty

- **Decision**: Evaluate comments on every check cycle where new commits are detected (tracked by `ReviewPrScan`), and leave comments unresolved if the AI is unsure.
- **Rationale**: The background worker (crawler) runs periodically. If `LastProcessedCommitId` differs from the current PR head, the worker will trigger an evaluation. The prompt to the AI will explicitly state that if it cannot confidently determine that the issue is fully fixed, it should return a negative result (meaning the comment remains unresolved).