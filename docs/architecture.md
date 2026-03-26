# Architecture — Meister DEV's ProPR

## Table of Contents

- [System Context](#system-context)
- [Request Flow: POST /reviews](#request-flow-post-reviews)
- [Authentication Flow](#authentication-flow)
- [Job State Machine](#job-state-machine)
- [Credential Resolution](#credential-resolution)
- [PR Crawler Flow](#pr-crawler-flow)
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

    subgraph backend["Meister DEV's ProPR Backend"]
        API["ASP.NET Core API"]
        WORKER["ReviewJobWorker (BackgroundService)"]
        CRAWLER["PrCrawlerWorker (BackgroundService)"]
    end

    EXT -- "POST /reviews X-Client-Key + X-Ado-Token" --> API
    API -- "persist job" --> PG
    WORKER -- "poll every 2 s" --> PG
    WORKER -- "fetch PR diff" --> ADO
    WORKER -- "AI review (Responses API)" --> AOAI
    WORKER -- "post comment threads" --> ADO
    CRAWLER -- "poll open PRs" --> ADO
    CRAWLER -- "enqueue jobs" --> PG
```

---

## Request Flow: POST /reviews

The full lifecycle of a review request — from HTTP call to ADO comment.

```mermaid
sequenceDiagram
    actor Caller
    participant MW as ClientKeyMiddleware
    participant RC as ReviewsController
    participant JR as JobRepository
    participant WK as ReviewJobWorker
    participant PF as AdoPullRequestFetcher
    participant AI as AgentAiReviewCore
    participant CP as AdoCommentPoster

    Caller->>MW: POST /reviews (X-Client-Key, X-Ado-Token)
    MW->>MW: validate X-Client-Key
    MW->>RC: forward request
    RC->>RC: verify X-Ado-Token (identity check)
    RC->>JR: CreateOrGetActiveJobAsync()
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

## Authentication Flow

How Admin UI and API callers obtain and renew credentials.

```mermaid
sequenceDiagram
    actor User as User / Admin UI
    participant Auth as AuthController
    participant MW as AdminKeyMiddleware
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

### AdminKeyMiddleware — evaluation order

```mermaid
flowchart TD
    REQ([Inbound Request]) --> B1

    B1{"Authorization: Bearer JWT?"}-- valid JWT --> SET_JWT["Set UserId + IsAdmin from claims"]
    B1 -- no/invalid --> B2

    B2{"X-User-Pat header?"}-- PAT found & BCrypt match --> SET_PAT["Set UserId + IsAdmin from user record"]
    B2 -- no/invalid --> B3

    B3{"X-Admin-Key header?\n(legacy — deprecated)"}-- matches MEISTER_ADMIN_KEY --> WARN["Log deprecation warning\nSet IsAdmin = true"]
    B3 -- no --> DEFAULT["IsAdmin = false"]

    SET_JWT --> NEXT([next()])
    SET_PAT --> NEXT
    WARN --> NEXT
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

---

## Data Model

PostgreSQL entities and their relationships.

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

    Client ||--o| ClientAdoCredential : "has (optional)"
    Client ||--o{ CrawlConfiguration : "has"
    Client ||--o{ ReviewJob : "owns"
    Client ||--o{ UserClientRole : "scoped to"
    ReviewJob ||--o{ ReviewJobProtocol : "has passes"
    ReviewJob ||--o{ ReviewFileResult : "has file results"
    ReviewJobProtocol }o--|| ReviewFileResult : "linked to"
    AppUser ||--o{ UserClientRole : "has"
    AppUser ||--o{ UserPat : "has"
    AppUser ||--o{ RefreshToken : "has"
```
