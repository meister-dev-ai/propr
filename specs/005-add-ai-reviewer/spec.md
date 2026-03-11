# Feature Specification: Add AI Identity as Reviewer

**Feature Branch**: `005-add-ai-reviewer`
**Created**: 2026-03-11
**Status**: Draft
**Input**: User description: "Currently when we manually add a review job (not via crawling) the AI identity is not added as a reviewer to the pull request. Once the AI identity performs a PR we need to add it at least as an optional reviewer to the PR, so it is clear that it takes part in the review."

## Clarifications

### Session 2026-03-11

- Q: Where should the AI reviewer GUID be stored? → A: New nullable `ReviewerId` field on the `Client` entity / `clients` DB table; removed from `CrawlConfiguration`.
- Q: When a client has no `ReviewerId` configured, what should happen? → A: Fail the job (or reject before starting) with a log message; no comments posted.
- Q: Should the admin UI be updated as part of this feature? → A: Yes — regenerate `openapi.ts`, add a `ReviewerId` input field to `ClientDetailView`, and wire up `PUT /clients/{id}/reviewer-identity`.
- Q: How should the reviewer identity be input in the admin UI? → A: Via display name resolution — user enters an ADO org URL and a display name, the UI calls `GET /identities/resolve`, presents matching identities, user selects one, and the resolved GUID is saved via `PUT /clients/{id}/reviewer-identity`. Admin key bypasses client-key gate so no separate client key is needed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - AI Reviewer Visible on Pull Request (Priority: P1)

When the AI identity begins reviewing a pull request, team members viewing the PR can immediately see the AI as
a listed reviewer. This gives the team clarity that an AI review is underway and who is participating in the
code review process.

**Why this priority**: This is the core value of the feature — without visibility of the AI reviewer, users have no
clear indication that AI took part in the review, undermining trust and transparency.

**Independent Test**: Can be fully tested by triggering a review job on a PR and verifying the AI identity appears
in the reviewers list of that PR in Azure DevOps.

**Acceptance Scenarios**:

1. **Given** a review job is submitted manually for an open pull request, **When** the system begins processing the
   job, **Then** the AI identity is added as an optional reviewer on the pull request before any review comments
   are posted.
2. **Given** the AI identity is already listed as a reviewer on the pull request, **When** a review starts again
   (e.g., on a new iteration), **Then** the system does not add a duplicate reviewer entry and the job continues
   normally.
3. **Given** a review job is triggered via the PR crawler (automatic crawling), **When** the system begins
   processing the job, **Then** the AI identity is added as an optional reviewer on the pull request before any
   review comments are posted.

---

### User Story 2 - Graceful Handling When Reviewer Addition Fails (Priority: P2)

When the system attempts to add the AI identity as a reviewer but encounters an error (e.g., insufficient
permissions, PR already closed), the overall review job stops being processed and an error is left with the review job. 
Review comments are not posted and the failure is surfaced as an error in the job log.

**Why this priority**: Reviewer assignment is critical action.

**Independent Test**: Can be tested by simulating a permission failure for reviewer assignment and verifying that
review comments are not posted and the job terminates with a failure status.

**Acceptance Scenarios**:

1. **Given** the AI identity can't be added as reviewer,  **Then** Comments are not added as well.

---

### User Story 3 - Configure AI Reviewer Identity for a Client (Priority: P2)

An administrator sets the AI reviewer GUID on a client record so that review jobs for that
client can add the AI as a reviewer. Without this configuration step, all review jobs for
the client will be rejected.

**Why this priority**: Prerequisite for User Story 1 — no reviewer can be added unless the
identity is configured. However, existing clients with crawl configurations already have a
`ReviewerId` that can be migrated, so this story is primarily needed for new clients and
migration.

**Independent Test**: Can be tested by calling the client update API with a valid reviewer
GUID and verifying the value is persisted and returned in subsequent client reads.

**Acceptance Scenarios**:

1. **Given** an existing client has no `ReviewerId` set, **When** an admin sets a valid ADO
   identity GUID via the client management API, **Then** the value is persisted and subsequent
   review jobs for that client proceed with reviewer addition.
2. **Given** an existing client has a `ReviewerId` set, **When** an admin updates it to a new
   GUID, **Then** the new value is used for all subsequent review jobs.
3. **Given** a client has no `ReviewerId` and a review job is submitted, **When** the worker
   picks up the job, **Then** the job is rejected with a log message indicating missing
   reviewer configuration.

---

### Edge Cases

- What happens when the client has no `ReviewerId` configured (null)? The job is rejected before processing begins;
  a log message is recorded and no comments are posted.
- What happens when the pull request is already completed or abandoned at the time reviewer assignment is attempted?
  The reviewer addition fails, the job is marked as failed, and no review comments are posted.
- What happens if the AI identity does not have permission to modify reviewers on the PR? The job fails and no
  review comments are posted.
- What happens when the AI identity is already a reviewer on the PR? The system either skips the addition or the
  ADO service returns a no-op; no duplicate reviewer entry is created.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Before processing a review job, the system MUST check that the owning client has a `ReviewerId`
  configured. If not, the job MUST be rejected (not started) and the missing configuration MUST be logged.
- **FR-002**: Before posting any review comments, the system MUST add the AI identity (identified by the client's
  `ReviewerId`) as an optional reviewer to the pull request.
- **FR-003**: The reviewer addition MUST occur for both manually submitted review jobs and automatically crawled
  review jobs.
- **FR-004**: The reviewer addition MUST be idempotent — if the AI identity is already listed as a reviewer, no
  duplicate entry is created and the job continues normally.
- **FR-005**: If the reviewer addition fails for any reason (permissions, closed PR, network error), the review
  job MUST be marked as failed, no review comments MUST be posted, and the failure reason MUST be recorded in the
  job log.
- **FR-006**: The AI reviewer identity GUID MUST be stored at the client level (one value per client), not per
  crawl configuration. The `CrawlConfiguration` entity MUST NOT carry a `ReviewerId` field.
- **FR-007**: The system MUST log the outcome of the reviewer addition attempt (success or failure with reason).

### Key Entities

- **Client**: Represents a registered consumer of the system. Gains a new `ReviewerId` field — the ADO identity
  GUID of the AI service account for that client. Nullable; a null value means reviewer addition is not configured
  and review jobs will be rejected until it is set.
- **CrawlConfiguration**: Loses its existing `ReviewerId` field — reviewer identity is no longer stored per crawl
  configuration.
- **Review Job**: Represents a single review task for a pull request; has a status (Pending → Processing →
  Completed | Failed) and an associated log of events.
- **AI Identity**: The service account or service principal used by the system to interact with ADO; identified by
  its ADO identity GUID stored on the owning `Client` record.
- **Pull Request Reviewer**: A participant on a pull request in ADO; can be required or optional; the AI identity
  is added as an optional reviewer using the `ReviewerId` from the client record.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After a review job completes, the AI identity appears as an optional reviewer on the pull request in
  100% of cases where the ADO call succeeds.
- **SC-002**: When reviewer addition fails, the review job is always marked as failed and no review comments are
  posted — the outcome is consistent and predictable.
- **SC-003**: The AI identity is never listed as a duplicate reviewer on any pull request regardless of how many
  review iterations are performed.
- **SC-004**: Operators can confirm the reviewer addition outcome for any review job by inspecting logs or job
  details without requiring direct ADO access.

## Assumptions

- The ADO service principal / PAT used to post comments must have sufficient permissions to add optional reviewers
  to pull requests. If not, review jobs will fail per FR-005 until permissions are corrected.
- Adding a reviewer is supported by the existing ADO client library already in use in the project.
- "Optional reviewer" is the appropriate reviewer vote weight (as opposed to "required"); this is the least
  intrusive way to record AI participation without blocking merge.
- A database schema change is required: a new nullable `reviewer_id` column on the `clients` table, and the
  existing `reviewer_id` column on `crawl_configurations` is removed.
- The admin UI MUST be updated: regenerate the TypeScript API client from the new `openapi.json`, and add a
  reviewer identity resolution form to the client detail view — user inputs an ADO org URL and a display name,
  the UI calls `GET /identities/resolve`, shows matches, and saves the selected GUID via
  `PUT /clients/{id}/reviewer-identity`. The `X-Admin-Key` already bypasses client-key auth on that endpoint.
- The feature applies uniformly to all clients; there is no per-client opt-out toggle in scope for this iteration.
