# Feature Specification: MVP Backend — Local AI Code Review

**Feature Branch**: `001-mvp-backend`
**Created**: 2026-03-02
**Status**: Draft
**Input**: User description: "We want to implement a MVP for the backend that can be run
locally. The AI endpoint to use is given via environment variable. We perform a very
simple review by using the azure devops SDK to pull the files from the PR. The PR data
and structure and logic lives in the domain, which utilizes an abstracted AI review core
(the AI review core is implemented in the infrastructure and uses default .net library
and APIs for agentic coding)."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Submit Pull Request for AI Review (Priority: P1)

A developer triggers an AI code review for an open pull request. They supply their ADO
access token (used only to confirm they are an authenticated member of the target ADO
organisation) and PR identifiers; the backend accepts the request immediately and begins
processing asynchronously under its own identity, returning a job ID for later polling.

**Why this priority**: This is the entire product value. Without successful review
submission and result retrieval the system delivers nothing.

**Independent Test**: With a valid client key and ADO token configured, submit a POST
/reviews request for a real (or stubbed) PR. Receive a 202 Accepted response with a
jobId. Poll until status is "completed" and a non-empty summary is returned.

**Acceptance Scenarios**:

1. **Given** a valid client key and a valid ADO token, **When** POST /reviews is called
   with a well-formed PR identifier payload, **Then** a `202 Accepted` response is
   returned containing a unique `jobId`.
2. **Given** a submitted job, **When** the background worker picks it up, **Then** it
   fetches the changed files for the specified PR iteration from Azure DevOps using the
   backend's own managed identity credentials, not the user's token.
3. **Given** the changed files have been fetched, **When** the AI review runs, **Then**
   the job transitions to `completed` and the result contains an overall summary and
   zero or more per-file comments.
4. **Given** a missing or invalid client key, **When** any endpoint is called, **Then**
   `401 Unauthorized` is returned before any business logic executes.
5. **Given** a PR with no changed files, **When** the review runs, **Then** the job
   completes with an empty comment list and a summary noting no changes were found.

---

### User Story 2 - Poll for Review Status and Results (Priority: P1)

After submitting a review request the developer polls for the job's status. The system
reports the current processing stage and, once finished, returns the full review result
or an error message.

**Why this priority**: Polling is the only mechanism by which the extension retrieves
results; it is inseparable from User Story 1 in providing end-to-end value.

**Independent Test**: With a known `jobId`, repeatedly call GET /reviews/{jobId} and
observe the status field progressing through `pending` → `processing` → `completed` (or
`failed`). Verify result payload on completion.

**Acceptance Scenarios**:

1. **Given** a newly submitted job, **When** GET /reviews/{jobId} is called before
   processing begins, **Then** status is `"pending"`.
2. **Given** a job currently being processed, **When** GET /reviews/{jobId} is called,
   **Then** status is `"processing"`.
3. **Given** a completed job, **When** GET /reviews/{jobId} is called, **Then** status
   is `"completed"` and the response includes `result.summary` and `result.comments`.
4. **Given** a failed job, **When** GET /reviews/{jobId} is called, **Then** status is
   `"failed"` and a human-readable `error` message is present.
5. **Given** an unknown `jobId`, **When** GET /reviews/{jobId} is called, **Then**
   `404 Not Found` is returned.

---

### User Story 3 - List Review History (Priority: P2)

The extension overview panel shows all review jobs submitted under the current client
key, so the developer can see past and in-progress reviews at a glance.

**Why this priority**: Useful but not blocking — the core review flow works without it.

**Independent Test**: After submitting two or more reviews, call GET /reviews and verify
all jobs are present in newest-first order with correct status values.

**Acceptance Scenarios**:

1. **Given** multiple submitted reviews for the same client key, **When** GET /reviews
   is called, **Then** all jobs are returned in descending `submittedAt` order.
2. **Given** no reviews have been submitted, **When** GET /reviews is called, **Then**
   an empty array is returned.

---

### User Story 4 - Run Backend Locally with Minimal Configuration (Priority: P1)

A developer sets up the backend on their local machine by providing the AI endpoint URL,
a valid client key, and ADO identity credentials, then starts the service. No database,
message broker, or other external infrastructure is required.

**Why this priority**: The stated MVP goal. If the backend cannot be started locally
with minimal config, all other stories are blocked.

**Independent Test**: Set the AI endpoint and client key environment variables, run the
backend, call GET /healthz, and receive `200 OK`. Then submit a full review cycle.

**Acceptance Scenarios**:

1. **Given** an AI endpoint URL, a client key, and Azure service principal credentials
   (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET`) configured as
   environment variables, **When** the backend starts locally, **Then** it is fully
   operational with no external services or filesystem writes required.
2. **Given** the backend is running, **When** GET /healthz is called, **Then** `200 OK`
   is returned with a body indicating the service is ready.
3. **Given** a missing AI endpoint environment variable, **When** the backend starts,
   **Then** startup fails with a clear configuration error message.

---

### Edge Cases

- What happens when the `X-Ado-Token` is expired or invalid at request time? → The
  user verification step fails and the request is rejected with `401 Unauthorized`
  before a job is created.
- What happens when the backend's managed identity lacks permissions to read the PR's
  repository? → Job transitions to `failed` with a descriptive ADO authorization error.
- What happens when the AI endpoint is unreachable or returns an error? → Job
  transitions to `failed`; the worker does not crash.
- What happens when the same PR iteration is submitted a second time while the first
  job is still non-failed? → The existing `jobId` is returned; no duplicate job is
  created.
- What happens when the PR is very large and the AI call fails due to payload size?
  → No hard limit is enforced in the MVP; the AI call failure surfaces naturally as
  a job transition to `failed` with the error message from the AI endpoint.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST accept review requests containing an ADO organisation URL,
  project ID, repository ID, pull request ID, and iteration ID.
- **FR-002**: System MUST validate the `X-Client-Key` header on every request; return
  `401 Unauthorized` for missing or unrecognised keys before any business logic runs.
- **FR-015**: System MUST validate the `X-Ado-Token` header to confirm the requesting
  user is an authenticated member of the configured ADO organisation; the token is used
  solely for this identity verification check and MUST NOT be used for any ADO API
  operations (file fetching, comment posting, or any other calls).
- **FR-003**: System MUST fetch, for each changed file in the specified PR iteration,
  the full file content AND the unified diff/patch using the backend's own managed
  identity credentials; both are passed to the AI review core to maximise review
  quality and enable accurate line-level comment placement.
- **FR-004**: System MUST route the fetched PR data through the domain's review
  orchestration via an `IAiReviewCore` interface defined in the domain layer; the
  interface contract MUST NOT expose or reference any AI SDK types — it accepts only
  domain entities and returns a `ReviewResult`.
- **FR-005**: System MUST perform the AI review using `Microsoft.Agents.AI` as the
  infrastructure implementation behind `IAiReviewCore`; the agent endpoint URL and
  any required credentials MUST be supplied exclusively via environment variables and
  MUST NOT be visible to or suppliable by the caller.
- **FR-006**: System MUST produce review results consisting of a narrative summary and
  zero or more comments, each with an optional file path, optional line number,
  severity (`info` / `warning` / `error` / `suggestion`), and a message.
- **FR-007**: System MUST store submitted jobs in-memory and return a unique job ID
  within one second of receiving a valid submission request.
- **FR-008**: System MUST expose the status of any job (pending / processing /
  completed / failed) for retrieval by job ID.
- **FR-009**: System MUST expose a list of all jobs for the authenticated client key,
  ordered newest first.
- **FR-010**: System MUST expose a `/healthz` endpoint that returns `200 OK` when the
  service is ready to accept and process requests.
- **FR-011**: System MUST be fully configurable via environment variables (AI endpoint
  URL, valid client keys, and Azure identity credentials) and MUST start without any
  database or external service; in production the backend authenticates to ADO via
  managed identity (`DefaultAzureCredential`); for local development a service principal
  is used by providing `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_CLIENT_SECRET`
  as environment variables, which `DefaultAzureCredential` picks up automatically.
- **FR-014**: The background worker MUST process all pending jobs concurrently with no
  artificial parallelism cap; concurrency limits are deferred to a future iteration.
- **FR-012**: Submitting a review for a PR iteration that already has a non-failed job
  MUST return the existing `jobId` rather than creating a new job.
- **FR-013**: System MUST post each review finding back to the ADO pull request as an
  inline comment thread anchored to the specific file and line number where available;
  findings with no file attribution MUST be posted as PR-level (general) comment
  threads; all postings MUST be made using the backend's managed identity so that
  comments appear as the "Meister ProPR" principal, not the requesting user.

### Key Entities

- **ReviewJob**: Represents a single AI review request; identified by a unique ID;
  carries status, submission timestamp, completion timestamp, PR identifiers, and an
  optional result or error message.
- **PullRequest**: Domain representation of an ADO pull request for a given iteration;
  carries PR metadata (title, description, source/target branch) and the ordered list
  of changed files.
- **ChangedFile**: A single file modified in the PR iteration; carries the file path,
  change type (add / edit / delete), the full file content, and the unified diff/patch;
  both content forms are provided to give the AI complete context and enable accurate
  line-number attribution.
- **ReviewResult**: The output produced by the AI review; carries a narrative summary
  string and a (possibly empty) list of ReviewComments.
- **ReviewComment**: A single AI-generated finding; carries an optional file path,
  optional line number, severity level, and message text.
- **ClientRegistration**: An authorised client identity; for MVP sourced from
  environment variable configuration; uniquely identified by its key value.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can start the backend locally by setting the AI endpoint URL,
  client key, and Azure service principal env vars, and receive a successful health
  check within 30 seconds, with no other infrastructure setup required.
- **SC-002**: A review submission request (POST /reviews) receives a `202 Accepted`
  response with a `jobId` within 1 second.
- **SC-003**: A review of a pull request with up to 20 changed files completes
  (status `completed` or `failed`) within 120 seconds of submission.
- **SC-004**: The review result for a pull request containing an intentional code
  quality issue includes at least one comment identifying the issue (manual acceptance
  test).
- **SC-005**: Requests with an invalid or missing client key are rejected with `401`
  in 100% of cases with no business logic executed.
- **SC-006**: Resubmitting a review for the same PR iteration returns the original
  `jobId` — no duplicate jobs are created.
- **SC-007**: The backend remains responsive to health check and polling requests
  while multiple review jobs are being processed concurrently with no imposed limit.

## Clarifications

### Session 2026-03-02

- Q: Should `ChangedFile` carry full file content, unified diff, or both? → A: Both — full file content plus the unified
  diff, to maximise AI review quality and enable accurate line-number attribution.
- Q: How should AI review findings be posted back to the ADO pull request? → A: Inline thread per finding anchored to
  file + line where available; PR-level thread fallback for findings with no file attribution.
- Q: What AI SDK backs the `IAiReviewCore` infrastructure implementation? → A: `Microsoft.Agents.AI` (Foundry); the
  domain interface MUST NOT leak SDK types — accepts domain entities, returns `ReviewResult` only.
- Q: Should the MVP enforce a hard PR size limit? → A: No — no hard limit for MVP; oversized AI calls fail naturally and
  surface as a `failed` job with the AI endpoint's error message.
- Q: How many jobs should the background worker process concurrently? → A: Unbounded — all pending jobs start
  immediately in parallel; concurrency limits deferred to a future iteration.
- Q: Should the backend use the user's ADO token for ADO API operations? → A: No — the backend authenticates to ADO
  using its own managed identity ("Meister ProPR"); the `X-Ado-Token` is used only to verify the requesting user is an
  authenticated ADO org member and is never forwarded or used for any API calls.
- Q: How does the backend authenticate to ADO as "Meister ProPR" in local development where managed identity is
  unavailable? → A: `DefaultAzureCredential` — uses managed identity in Azure automatically; for local dev, a service
  principal is configured via `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_CLIENT_SECRET` env vars,
  which `DefaultAzureCredential` resolves without code changes.

## Assumptions

- The MVP does not require multi-tenant isolation; a single valid client key in
  environment configuration is sufficient.
- Jobs lost on process restart are acceptable for MVP; durability is deferred.
- The AI review prompt and scope will be determined during the planning phase; the
  spec treats the review output format as fixed (summary + comments) but leaves
  prompt content to implementation.
- The `X-Ado-Token` is a valid ADO token used only to verify the requesting user's
  identity; it grants no ADO permissions to the backend.
- The managed identity ("Meister ProPR") is assumed to have been granted the necessary
  ADO permissions (read PR metadata and file contents, post PR comments) for the target
  organisation and project prior to deployment.
