# ProCursor Architecture

This page covers the ProCursor bounded slice: how review orchestration reaches it, how tracked
sources are refreshed, and how ProCursor token usage is captured and rolled up.

## Boundary And Runtime Position

ProCursor runs inside the same deployment today, but it is treated as a bounded slice with its own
facade and options surface. Review orchestration reaches it only through `IProCursorGateway` and
`PROCURSOR_*` settings; it does not talk directly to ProCursor repositories, Azure DevOps
materializers, or snapshot tables.

For guided admin flows, the same gateway boundary owns save-time validation of
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

## Refresh Flow

Tracked branches refresh independently from the pull-request worker. The scheduler polls branch
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

## Token Reporting

ProCursor token reporting runs alongside the indexing flow. The capture boundary prefers
provider-reported usage metadata returned by the AI client, falls back to tokenizer-based estimates
when needed, and stores one idempotent event row per physical ProCursor AI call.

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

A dedicated rollup worker refreshes daily and monthly aggregates so the admin UI can read stable
totals while still gap-filling the newest uncaptured window from raw events.
