# Architecture — Meister DEV's ProPR

## Table of Contents

- [System Context](#system-context)
- [Vertical Slice Composition](#vertical-slice-composition)
- [Review Trigger Flow](#review-trigger-flow)
- [Review Dedup Flow](#review-dedup-flow)
- [Authentication Flow](#authentication-flow)
- [Job State Machine](#job-state-machine)
- [Credential Resolution](#credential-resolution)
- [Guided ADO Configuration](#guided-ado-configuration)
- [PR Crawler Flow](#pr-crawler-flow)
- [Crawl Source Scope Snapshotting](#crawl-source-scope-snapshotting)
- [Token Optimization Pipeline](#token-optimization-pipeline)
- [ProCursor Boundary](#procursor-boundary)
- [ProCursor Refresh Flow](#procursor-refresh-flow)
- [ProCursor Token Reporting](#procursor-token-reporting)
- [Data Model](#data-model)

---

## System Context

Who communicates with whom at the boundary level.

```mermaid
flowchart TD
    ADO["Azure DevOps (PR + Comments)"]
    AOAI["Azure OpenAI / AI Foundry"]
    EXT["External Caller (ADO Extension / CI)"]
    PG[("PostgreSQL")]
    ADMINUI["Admin UI (Vue 3 SPA)"]

    subgraph backend["Meister DEV's ProPR Backend"]
        API["ASP.NET Core API"]
        WORKER["ReviewJobWorker (BackgroundService)"]
        CRAWLER["PrCrawlerWorker (BackgroundService)"]
        PROCURSOR["ProCursor Module\n(Gateway + Index Worker)"]
    end

    EXT -- "POST /clients/{clientId}/reviewing/jobs X-User-Pat + X-Ado-Token" --> API
    ADMINUI -- "Admin API / JWT" --> API
    API -- "persist job" --> PG
    WORKER -- "poll every 2 s" --> PG
    WORKER -- "fetch PR diff" --> ADO
    WORKER -- "AI review (Chat API)" --> AOAI
    WORKER -- "knowledge + symbol lookups" --> PROCURSOR
    WORKER -- "post comment threads" --> ADO
    CRAWLER -- "poll open PRs" --> ADO
    CRAWLER -- "enqueue jobs" --> PG
    PROCURSOR -- "persist snapshots + jobs" --> PG
    PROCURSOR -- "materialize tracked branches" --> ADO
    PROCURSOR -- "embedding generation" --> AOAI
```

> **Authentication note:** Review submission uses a user credential (`Authorization: Bearer ...`
> or `X-User-Pat`) plus `X-Ado-Token`; status polling on `/reviewing/jobs/{jobId}/status` uses `X-Ado-Token`.
> See the [Authentication Flow](#authentication-flow) section for how JWTs and PATs are resolved.

## Vertical Slice Composition

The backend startup path now separates shared support from feature-owned module registration. `Program.cs` composes the application through one shared support entry point plus explicit module entry points so feature ownership is visible at the composition root.

| Entry Point | Responsibility |
|-------------|----------------|
| `AddInfrastructureSupport()` | Shared EF Core setup, Azure credential resolution, ADO transport, AI client plumbing, options binding, and secret protection |
| `AddReviewingModule()` | Review intake, orchestration, diagnostics, and thread-memory infrastructure |
| `AddCrawlingModule()` | Crawl configuration, discovery, and PR scan execution infrastructure |
| `AddClientsModule()` | Client administration and AI connection persistence |
| `AddIdentityAndAccessModule()` | User auth, PATs, refresh tokens, password hashing, and bootstrap services |
| `AddMentionsModule()` | Mention scan, reply, and AI answer composition |
| `AddPromptCustomizationModule()` | Prompt override persistence and application services |
| `AddUsageReportingModule()` | Client and ProCursor usage reporting services |
| `AddProCursorModule()` | ProCursor indexing, graph extraction, and query composition |

This composition model is the enforcement point for the vertical-slice migration: feature-owned registrations should move into their module roots while shared support stays cross-cutting and feature-agnostic.

DB-backed registrations are enabled only when the effective DB mode is on. In `Testing`, that requires `TEST_ENABLE_DB_MODE=true` even if `DB_CONNECTION_STRING` is present, which keeps in-memory test hosts from accidentally pulling in PostgreSQL-only services.

---

## Review Trigger Flow

The full lifecycle of a review request — from HTTP call to ADO comment.

```mermaid
sequenceDiagram
    actor Caller
    participant MW as AuthMiddleware
    participant RC as ReviewJobsController
    participant JR as JobRepository
    participant WK as ReviewJobWorker
    participant PF as AdoPullRequestFetcher
    participant AI as AgentAiReviewCore
    participant CP as AdoCommentPoster

    Caller->>MW: POST /clients/{clientId}/reviewing/jobs (JWT or X-User-Pat, X-Ado-Token)
    MW->>MW: resolve UserId, IsAdmin, ClientRoles
    MW->>RC: forward request
    RC->>RC: require ClientAdministrator for {clientId}
    RC->>RC: verify X-Ado-Token (identity check)
    RC->>JR: FindActiveJob()/AddAsync()
    JR-->>RC: jobId (new or existing)
    RC-->>Caller: 202 Accepted { jobId }

    loop every 2 seconds
        WK->>JR: DequeueNextPendingAsync()
        JR-->>WK: ReviewJob
        WK->>PF: FetchPullRequestAsync()
        PF-->>WK: PR diff + file contents
        WK->>AI: ReviewAsync(pr, files)
        AI-->>WK: ReviewResult (summary + comments)
        WK->>CP: PostCommentsAsync(result)
        CP-->>WK: done
        WK->>JR: MarkCompletedAsync(jobId, result)
    end
```

---

## Review Dedup Flow

Incremental review publication uses a two-stage duplicate-suppression path before any new PR thread is created.

1. `ReviewOrchestrationService.BuildReviewContextAsync(...)` carries forward completed per-file results from the previous reviewed iteration for unchanged files.
2. `FileByFileReviewOrchestrator.SynthesizeResultsAsync(...)` excludes `IsCarriedForward` file results from synthesis summaries, cross-file deduplication, and quality-filter input, while preserving `CarriedForwardFilePaths` and a carried-forward skip count on the final `ReviewResult`.
3. `ReviewOrchestrationService.PublishReviewResultAsync(...)` opens a dedicated `ReviewJobProtocol` pass labeled `posting`, calls `IAdoCommentPoster.PostAsync(...)`, persists the posted `ReviewResult`, and records aggregate duplicate-suppression diagnostics.
4. `AdoCommentPoster` evaluates each candidate finding against existing bot-authored PR threads using:
    - normalized file-path and anchor matching for equivalent locations,
    - resolved-thread reuse for previously raised concerns,
    - exact normalized-text matching,
    - thread-memory similarity scoped to the current pull request,
    - deterministic text-similarity fallback when historical memory signals are degraded.
5. The posting protocol emits `dedup_summary` on every posting pass and `dedup_degraded_mode` only when historical duplicate protection had to fall back to reduced checks.

This keeps incremental reviews additive: unchanged findings remain visible in the stored result and summary context, but only genuinely fresh findings are allowed to create new ADO threads.

---

## Authentication Flow

How Admin UI and API callers obtain and renew credentials.

```mermaid
sequenceDiagram
    actor User as User / Admin UI
    participant Auth as AuthController
    participant MW as AuthMiddleware
    participant JT as JwtTokenService
    participant UR as UserRepository
    participant PR as UserPatRepository

    User->>Auth: POST /auth/login { username, password }
    Auth->>UR: GetByUsernameAsync()
    UR-->>Auth: AppUser (BCrypt password hash)
    Auth->>Auth: BCrypt.Verify(password, hash)
    Auth->>JT: GenerateAccessToken(user)
    Auth->>UR: persist RefreshToken (SHA-256 hash, 7 days)
    Auth-->>User: { accessToken (15 min), refreshToken (7 days) }

    Note over User,Auth: Silent refresh within 60 s of expiry

    User->>Auth: POST /auth/refresh { refreshToken }
    Auth->>UR: GetActiveByHashAsync(sha256(token))
    Auth->>JT: GenerateAccessToken(user)
    Auth-->>User: { accessToken (15 min) }
```

### AuthMiddleware — evaluation order

```mermaid
flowchart TD
    REQ([Inbound Request]) --> B1

    B1{"Authorization: Bearer JWT?"}-- valid JWT --> SET_JWT["Set UserId + IsAdmin from claims\nLoad ClientRoles from DB"]
    B1 -- no/invalid --> B2

    B2{"X-User-Pat header?"}-- PAT found & BCrypt match --> SET_PAT["Set UserId + IsAdmin from user record\nLoad ClientRoles from DB"]
    B2 -- no/invalid --> DEFAULT["Anonymous request\nIsAdmin = false"]

    SET_JWT --> NEXT([next()])
    SET_PAT --> NEXT
    DEFAULT --> NEXT
```

---

## Job State Machine

All possible states of a `ReviewJob` and their transitions.

```mermaid
stateDiagram-v2
    [*] --> Pending : POST /reviews (job created)
    Pending --> Processing : Worker dequeues job
    Processing --> Completed : AI review posted to ADO
    Processing --> Failed : Exception / ADO error
    Failed --> Pending : Retry (if retry count < max)
    Processing --> Failed : Stuck — no heartbeat > StuckJobTimeoutMinutes
    Completed --> [*]
    Failed --> [*] : Max retries exceeded

    note right of Processing
        AdoPullRequestFetcher
        FileByFileReviewOrchestrator
        AdoCommentPoster
    end note

    note left of Failed
        ReviewJobWorker cleanup
        runs on startup + every 10 min
    end note
```

---

## Credential Resolution

How the backend picks the Azure credential for each ADO operation.

```mermaid
flowchart TD
    START([ADO Operation for Client X]) --> LOOKUP

    LOOKUP{Per-client credentials in DB?}

    LOOKUP -- "yes" --> CSC["ClientSecretCredential (tenantId, clientId, secret)"]
    LOOKUP -- "no" --> DAC["DefaultAzureCredential (env vars / managed identity)"]

    CSC --> CACHE_C["VssConnection (client-scoped cache)"]
    DAC --> CACHE_G["VssConnection (global cache)"]

    CACHE_C --> ADO[Azure DevOps API]
    CACHE_G --> ADO
```

---

## Guided ADO Configuration

Guided Azure DevOps onboarding separates credential ownership from admin selections. Client
credentials authorize the backend to talk to Azure DevOps. Client-scoped organization-scope
records define which Azure DevOps organizations administrators may choose in guided ProCursor and
crawl-config flows. All downstream project, repository, wiki, branch, and crawl-filter choices are
resolved through discovery endpoints and revalidated again at save time.

```mermaid
sequenceDiagram
    actor Admin
    participant UI as Admin UI
    participant CC as ClientsController
    participant DR as AdoDiscoveryController
    participant OS as ClientAdoOrganizationScopeRepository
    participant DS as IAdoDiscoveryService
    participant PC as ProCursorKnowledgeSourcesController
    participant PG as IProCursorGateway
    participant CFG as AdminCrawlConfigsController

    Admin->>UI: Configure client ADO credentials
    Admin->>UI: Add allowed organization
    UI->>CC: POST /clients/{id}/ado-organization-scopes
    CC->>OS: Store normalized org URL + verification state
    UI->>DR: GET /admin/clients/{id}/ado/discovery/projects
    DR->>DS: Resolve live projects for organizationScopeId
    UI->>DR: GET discovery sources / branches / crawl-filters
    DR->>DS: Resolve live source metadata
    UI->>PC: POST guided ProCursor source
    PC->>PG: CreateSourceAsync(... organizationScopeId, canonicalSourceRef ...)
    PG->>DS: Revalidate project/source/branch
    UI->>CFG: POST guided crawl configuration
    CFG->>DS: Revalidate selected crawl filters
    CFG-->>UI: 201 or actionable 4xx/409 drift error
```

Compatibility remains in place for legacy callers that still send raw `organizationUrl` and
repository identifiers, but the guided path is now the primary architecture boundary.

---

## PR Crawler Flow

The background crawler finds new PRs automatically — no external trigger needed.

```mermaid
sequenceDiagram
    participant CW as PrCrawlerWorker
    participant CR as CrawlConfigRepository
    participant PF as AdoPullRequestFetcher
    participant JR as JobRepository

    loop every PR_CRAWL_INTERVAL_SECONDS (default 60 s)
        CW->>CR: GetAllActiveConfigsAsync()
        CR-->>CW: List<CrawlConfiguration>

        loop for each CrawlConfiguration
            CW->>PF: GetOpenPrsForReviewerAsync(org, project, reviewer)
            PF-->>CW: List<PullRequest>

            loop for each PR
                CW->>JR: CreateOrGetActiveJobAsync(pr)
                JR-->>CW: jobId (skips if already active)
            end
        end
    end
```

The crawler now operates against crawl configurations that can either reference all client
ProCursor sources or a selected subset. That selection is durable and does not rely on reading the
latest admin configuration during review execution.

---

## Crawl Source Scope Snapshotting

When a crawl configuration uses `selectedSources`, `PrCrawlService` copies that source list onto
the queued `ReviewJob`. `ReviewOrchestrationService` later consumes the saved snapshot so an admin
change made after queue time cannot silently alter the knowledge scope of in-flight work.

```mermaid
sequenceDiagram
    participant CW as PrCrawlerWorker
    participant PCS as PrCrawlService
    participant CR as CrawlConfigRepository
    participant JR as JobRepository
    participant SNAP as review_job_procursor_source_scopes
    participant ROS as ReviewOrchestrationService
    participant PG as IProCursorGateway

    CW->>PCS: Queue review for matching PR
    PCS->>CR: Load crawl config + selected ProCursor sources
    PCS->>JR: Persist ReviewJob
    PCS->>SNAP: Persist selected source IDs (optional)
    ROS->>JR: Load ReviewJob
    ROS->>SNAP: Load snapshotted source IDs
    ROS->>PG: Query knowledge with snapshotted scope
```

---

## Token Optimization Pipeline

Several techniques work together to minimise AI token consumption per review.

### 1 — File exclusion

Before any AI calls are made, `FileByFileReviewOrchestrator` applies `ReviewExclusionRules`
to every changed file:

```mermaid
flowchart TD
    START([Changed files in PR]) --> RULES

    RULES{"File matches\nexclusion pattern?"}
    RULES -- "yes" --> EXCL["MarkExcluded(pattern)\nProtocol: status=Excluded, 0 tokens"]
    RULES -- "no" --> REVIEW["Dispatch to IAiReviewCore"]

    EXCL --> SYNTH
    REVIEW --> SYNTH

    SYNTH([Synthesis — non-excluded files only])
```

Exclusion patterns are read from `.meister-propr/exclude` on the target branch. If the file
is absent, the built-in defaults apply (`**/Migrations/*.Designer.cs`,
`**/Migrations/*ModelSnapshot.cs`). An empty file disables all exclusions.

### 2 — Diff-only review messages

The per-file review input contains only the unified diff for that file. Full file content is
omitted; the AI is instructed to call the existing `get_file_content` tool if it needs more
context. This is the single biggest token saving for large files.

### 3 — System prompt pruning in review loops

`ToolAwareAiReviewCore` structures each file's multi-step review as:

- **Step 1**: Global system prompt (S1) + per-file context prompt (S2) + user message
- **Step 2+**: Per-file context prompt (S2) only + accumulated conversation

S1 (reviewer persona, tool guidance) is a fixed prefix — sending it only once lets the AI
infrastructure cache it across parallel file slots for the same PR.

### 4 — Tool result excerpt cap

When a review loop exceeds 3 steps, tool result text stored in the protocol is truncated to
1 000 characters and marked `[TRUNCATED]`. This prevents very deep loops from accumulating
unbounded amounts of raw file content in the conversation history.

---

## ProCursor Boundary

ProCursor runs inside the same deployment today, but it is treated as a bounded slice with its own
facade and options surface. Review orchestration reaches it only through `IProCursorGateway` and
`PROCURSOR_*` settings; it does not talk directly to ProCursor repositories, ADO materializers, or
snapshot tables.

For guided admin flows, the same gateway boundary also owns save-time validation of
`organizationScopeId`, canonical source references, and default or tracked branch selections. That
keeps Azure DevOps drift detection at the admin boundary instead of surfacing first during a later
refresh or review run.

```mermaid
flowchart LR
    REVIEW["Review orchestration"]
    GATEWAY["IProCursorGateway"]
    QUERY["ProCursorQueryService"]
    INDEX["ProCursorIndexCoordinator + ProCursorIndexWorker"]
    ADO["ADO materializers"]
    PG[("PostgreSQL + pgvector")]
    AOAI["Embedding endpoint"]

    REVIEW --> GATEWAY
    GATEWAY --> QUERY
    GATEWAY --> INDEX
    QUERY --> PG
    INDEX --> PG
    INDEX --> ADO
    INDEX --> AOAI
```

## ProCursor Refresh Flow

Tracked branches refresh independently from the pull request worker. The scheduler polls branch
heads, queues durable jobs, and the dedicated worker drains those jobs with per-source isolation so
one slow or failing source does not block unrelated repositories and wikis.

```mermaid
sequenceDiagram
    participant SCH as ProCursorRefreshScheduler
    participant ADO as Azure DevOps Git APIs
    participant JOBS as procursor_index_jobs
    participant WRK as ProCursorIndexWorker
    participant IDX as ProCursorIndexCoordinator
    participant PG as PostgreSQL + pgvector

    SCH->>ADO: Resolve tracked branch head
    ADO-->>SCH: Latest commit SHA
    SCH->>JOBS: Queue refresh job (deduped)
    WRK->>JOBS: Claim next pending job
    WRK->>IDX: Execute claimed job
    IDX->>ADO: Materialize repository/wiki state
    IDX->>PG: Persist snapshot, chunks, symbols
    IDX->>PG: Update freshness + latest indexed commit
```

## ProCursor Token Reporting

ProCursor token reporting runs alongside the indexing flow. The capture boundary prefers provider
usage metadata returned by the AI client, falls back to tokenizer-based estimates when needed, and
stores one idempotent event row per physical ProCursor AI call. A dedicated rollup worker refreshes
daily and monthly aggregates so the admin UI can read stable totals while still gap-filling the
newest uncaptured window from raw events.

```mermaid
sequenceDiagram
    participant IDX as ProCursorIndexCoordinator / QueryService
    participant EMB as ProCursorEmbeddingService
    participant REC as IProCursorTokenUsageRecorder
    participant EVT as procursor_token_usage_events
    participant WRK as ProCursorTokenUsageRollupWorker
    participant AGG as IProCursorTokenUsageAggregationService
    participant ROLL as procursor_token_usage_rollups
    participant API as ProCursorTokenUsageController
    participant UI as Admin UI

    IDX->>EMB: GenerateEmbeddingsAsync(...)
    EMB->>EMB: Prefer provider UsageDetails
    EMB->>EMB: Fallback to tokenizer estimate if missing
    EMB->>REC: RecordAsync(requestId, sourceId, model, token counts)
    REC->>EVT: Insert idempotent event row

    loop every PROCURSOR_TOKEN_USAGE_ROLLUP_POLL_SECONDS
        WRK->>AGG: RefreshRecentAsync()
        AGG->>EVT: Read touched event window
        AGG->>ROLL: Replace touched daily + monthly rollups
        WRK->>ROLL: Retention watermark (via purge service)
    end

    UI->>API: GET /admin/clients/{clientId}/procursor/...
    API->>ROLL: Read completed rollup buckets
    API->>EVT: Gap-fill newest uncaptured interval
    API-->>UI: Totals, top sources, recent safe events, freshness
```

---

## Data Model

PostgreSQL entities and their relationships.

### Guided Configuration Slice

The guided admin surface adds durable organization-scope state, canonical crawl filters, optional
selected-source associations, and review-job snapshots beneath the existing client boundary.

```mermaid
erDiagram
    Client ||--o{ ClientAdoOrganizationScope : "allows"
    Client ||--o{ CrawlConfiguration : "owns"
    Client ||--o{ ProCursorKnowledgeSource : "owns"
    ClientAdoOrganizationScope ||--o{ CrawlConfiguration : "selected by"
    CrawlConfiguration ||--o{ CrawlRepoFilter : "stores"
    CrawlConfiguration ||--o{ CrawlConfigurationProCursorSource : "limits to"
    ProCursorKnowledgeSource ||--o{ CrawlConfigurationProCursorSource : "referenced by"
    ReviewJob ||--o{ ReviewJobProCursorSourceScope : "snapshots"
    ProCursorKnowledgeSource ||--o{ ReviewJobProCursorSourceScope : "snapshotted"

    ClientAdoOrganizationScope {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string DisplayName
        string VerificationStatus
        bool IsEnabled
    }

    CrawlConfiguration {
        uuid Id PK
        uuid ClientId FK
        uuid OrganizationScopeId FK
        string OrganizationUrl
        string ProjectId
        string ProCursorSourceScopeMode
        int CrawlIntervalSeconds
    }

    CrawlRepoFilter {
        uuid Id PK
        uuid CrawlConfigurationId FK
        string DisplayName
        string CanonicalProvider
        string CanonicalValue
    }

    CrawlConfigurationProCursorSource {
        uuid CrawlConfigurationId FK
        uuid ProCursorKnowledgeSourceId FK
    }

    ReviewJobProCursorSourceScope {
        uuid ReviewJobId FK
        uuid ProCursorKnowledgeSourceId FK
    }
```

Read models surface invalid associations as `invalidProCursorSourceIds` instead of dropping them
silently, so administrators can repair stale selections from the guided UI.

### ProCursor Persistence Slice

The ProCursor tables hang off the existing client boundary and add their own durable job queue,
versioned snapshots, searchable chunks, and symbol graph rows.

```mermaid
erDiagram
    Client ||--o{ ProCursorKnowledgeSource : "owns"
    ProCursorKnowledgeSource ||--o{ ProCursorTrackedBranch : "tracks"
    ProCursorKnowledgeSource ||--o{ ProCursorIndexJob : "queues"
    ProCursorTrackedBranch ||--o{ ProCursorIndexSnapshot : "builds"
    ProCursorIndexSnapshot ||--o{ ProCursorKnowledgeChunk : "stores"
    ProCursorIndexSnapshot ||--o{ ProCursorSymbolRecord : "indexes"
    ProCursorIndexSnapshot ||--o{ ProCursorSymbolEdge : "relates"

    ProCursorKnowledgeSource {
        uuid Id PK
        uuid ClientId FK
        string DisplayName
        string SourceKind
        string RepositoryId
        string DefaultBranch
        bool IsEnabled
    }

    ProCursorTrackedBranch {
        uuid Id PK
        uuid KnowledgeSourceId FK
        string BranchName
        string LastSeenCommitSha
        string LastIndexedCommitSha
        bool IsEnabled
    }

    ProCursorIndexJob {
        uuid Id PK
        uuid KnowledgeSourceId FK
        uuid TrackedBranchId FK
        string JobKind
        string Status
        string DedupKey
        int AttemptCount
    }

    ProCursorIndexSnapshot {
        uuid Id PK
        uuid KnowledgeSourceId FK
        uuid TrackedBranchId FK
        string CommitSha
        string Status
        bool SupportsSymbolQueries
    }
```

```mermaid
erDiagram
    Client {
        uuid Id PK
        string Key "legacy plaintext (deprecated)"
        string KeyHash "BCrypt hash of active key"
        string PreviousKeyHash "BCrypt hash (7-day grace period after rotation)"
        datetime KeyExpiresAt
        datetime PreviousKeyExpiresAt
        datetime KeyRotatedAt
        int AllowedScopes
        string DisplayName
        bool IsActive
        datetime CreatedAt
    }

    ClientAdoCredential {
        uuid Id PK
        uuid ClientId FK
        string TenantId
        string AzureClientId
        string Secret "encrypted via ASP.NET Data Protection"
    }

    CrawlConfiguration {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string ProjectId
        int CrawlIntervalSeconds
        bool IsActive
    }

    ReviewJob {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string ProjectId
        string RepositoryId
        int PullRequestId
        int IterationId
        string Status
        datetime SubmittedAt
        datetime CompletedAt
        jsonb Result
        int RetryCount
        long TotalInputTokensAggregated
        long TotalOutputTokensAggregated
    }

    ReviewJobProtocol {
        uuid Id PK
        uuid JobId FK
        uuid FileResultId FK
        int PassNumber
        int InputTokens
        int OutputTokens
    }

    ReviewFileResult {
        uuid Id PK
        uuid JobId FK
        string FilePath
        bool IsComplete
        bool IsFailed
        bool IsExcluded "true when skipped by exclusion rules"
        string ExclusionReason "pattern that matched, e.g. **/Migrations/*.Designer.cs"
    }

    AppUser {
        uuid Id PK
        string Username "unique, case-insensitive"
        string PasswordHash "BCrypt"
        string GlobalRole "Admin / User"
        bool IsActive
        datetime CreatedAt
    }

    UserClientRole {
        uuid Id PK
        uuid UserId FK
        uuid ClientId FK
        string Role "ClientAdministrator / ClientUser"
        datetime AssignedAt
    }

    UserPat {
        uuid Id PK
        uuid UserId FK
        string TokenHash "BCrypt (mpr_ prefix)"
        string Label
        datetime ExpiresAt
        datetime CreatedAt
        datetime LastUsedAt
        bool IsRevoked
    }

    RefreshToken {
        uuid Id PK
        uuid UserId FK
        string TokenHash "SHA-256"
        datetime ExpiresAt
        datetime CreatedAt
        datetime RevokedAt
    }

    AiConnection {
        uuid Id PK
        uuid ClientId FK
        string DisplayName
        string EndpointUrl
        jsonb Models "array of model names"
        bool IsActive
        string ActiveModel
        string ApiKey "encrypted; null when using DefaultAzureCredential"
        datetime CreatedAt
        string ModelCategory "Standard / Reasoning"
    }

    PromptOverride {
        uuid Id PK
        uuid ClientId FK
        uuid CrawlConfigId FK "null for client-scope overrides"
        string Scope "ClientScope / CrawlConfigScope"
        string PromptKey "SystemPrompt | AgenticLoopGuidance | SynthesisSystemPrompt | QualityFilterSystemPrompt | PerFileContextPrompt"
        string OverrideText
        datetime CreatedAt
    }

    FindingDismissal {
        uuid Id PK
        uuid ClientId FK
        string PatternText "normalised, lowercase, max 200 chars"
        string Label "optional admin-provided note"
        string OriginalMessage "original AI finding message"
        datetime CreatedAt
    }

    ReviewPrScan {
        uuid Id PK
        uuid ClientId FK
        string RepositoryId
        int PullRequestId
        string LastProcessedCommitId
    }

    ReviewPrScanThread {
        uuid ReviewPrScanId FK
        int ThreadId
        int LastSeenReplyCount
    }

    Client ||--o| ClientAdoCredential : "has (optional)"
    Client ||--o{ CrawlConfiguration : "has"
    Client ||--o{ ReviewJob : "owns"
    Client ||--o{ UserClientRole : "scoped to"
    Client ||--o{ AiConnection : "has"
    Client ||--o{ PromptOverride : "has"
    Client ||--o{ FindingDismissal : "has"
    Client ||--o{ ReviewPrScan : "has"
    ReviewJob ||--o{ ReviewJobProtocol : "has passes"
    ReviewJob ||--o{ ReviewFileResult : "has file results"
    ReviewJobProtocol }o--|| ReviewFileResult : "linked to"
    AppUser ||--o{ UserClientRole : "has"
    AppUser ||--o{ UserPat : "has"
    AppUser ||--o{ RefreshToken : "has"
    ReviewPrScan ||--o{ ReviewPrScanThread : "has"
    CrawlConfiguration ||--o{ PromptOverride : "can have (crawl-config scope)"
```
