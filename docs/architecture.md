# Architecture — Meister ProPR

## Table of Contents

- [System Context](#system-context)
- [Request Flow: POST /reviews](#request-flow-post-reviews)
- [Clean Architecture Layers](#clean-architecture-layers)
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

    subgraph backend["Meister ProPR Backend"]
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

## Job State Machine

All possible states of a `ReviewJob` and their transitions.

```mermaid
stateDiagram-v2
    [*] --> Pending : POST /reviews (job created)
    Pending --> Processing : Worker dequeues job
    Processing --> Completed : AI review posted to ADO
    Processing --> Failed : Exception / ADO error
    Failed --> Pending : Retry (if retry count < max)
    Completed --> [*]
    Failed --> [*] : Max retries exceeded

    note right of Processing
        AdoPullRequestFetcher
        AgentAiReviewCore
        AdoCommentPoster
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
        string Key
        string DisplayName
        bool IsEnabled
        datetime CreatedAt
    }

    ClientAdoCredential {
        uuid Id PK
        uuid ClientId FK
        string TenantId
        string AzureClientId
        string Secret
    }

    CrawlConfiguration {
        uuid Id PK
        uuid ClientId FK
        string OrganizationUrl
        string ProjectId
        string ReviewerDisplayName
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
    }

    Client ||--o| ClientAdoCredential : "has (optional)"
    Client ||--o{ CrawlConfiguration : "has"
    Client ||--o{ ReviewJob : "owns"
```
