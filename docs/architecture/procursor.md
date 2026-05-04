# ProCursor Architecture

This page covers the ProCursor bounded slice: how review orchestration reaches it, how tracked
sources are refreshed, and how ProCursor token usage is captured and rolled up.

## Boundary And Runtime Position

ProCursor runs inside the same deployment as a bounded slice with its own facade and options
surface. Review orchestration reaches it only through `IProCursorGateway` and `PROCURSOR_*`
settings; it does not talk directly to ProCursor repositories, Azure DevOps materializers, or
snapshot tables.

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

## Review-Time Retrieval Path

Per-file review loops and the verification layer both reach ProCursor through the review-tools
boundary. The final finding gate does not call ProCursor directly; targeted evidence retrieval
executes upstream through the `IReviewContextTools` seam.

```mermaid
sequenceDiagram
    participant ORCH as ReviewOrchestrationService
    participant FACT as ProviderReviewContextToolsFactory
    participant LOOP as ToolAwareAiReviewCore
    participant VER as ReviewContextEvidenceCollector
    participant TOOLS as ProviderReviewContextToolsBase
    participant GW as IProCursorGateway
    participant QUERY as ProCursorQueryService

    ORCH->>FACT: Create(ReviewContextToolsRequest)
    FACT-->>ORCH: IReviewContextTools
    ORCH->>LOOP: ReviewAsync(..., ReviewSystemContext)
    LOOP->>TOOLS: ask_procursor_knowledge / get_procursor_symbol_info
    TOOLS->>GW: AskKnowledgeAsync(...) / GetSymbolInsightAsync(...)
    GW->>QUERY: Query repository-scoped chunks and symbols
    QUERY-->>GW: Answer or symbol insight
    GW-->>TOOLS: DTO
    TOOLS-->>LOOP: Tool result
    ORCH->>VER: CollectEvidenceAsync(work item, tools, modelId)
    VER->>TOOLS: get_changed_files / get_file_content / ask_procursor_knowledge / get_procursor_symbol_info
    TOOLS->>GW: Forward bounded repository-aware lookups
    GW->>QUERY: Query repository-scoped chunks and symbols
    QUERY-->>GW: Evidence DTOs
    GW-->>TOOLS: DTOs
    TOOLS-->>VER: Evidence items
```

1. `ReviewOrchestrationService.BuildReviewContextAsync(...)` snapshots PR identity, source branch,
   iteration, client id, and optional scoped ProCursor source ids into `ReviewContextToolsRequest`.
2. `ProviderReviewContextToolsFactory` selects the provider-specific implementation. Azure DevOps
   uses `AdoReviewContextToolsFactory`, and the same `IReviewContextTools` contract is preserved for
   the other providers.
3. `ToolAwareAiReviewCore` exposes `ask_procursor_knowledge` and `get_procursor_symbol_info`
   alongside file and tree lookup tools during each per-file review loop.
4. `ReviewContextEvidenceCollector` reuses the same `IReviewContextTools` boundary during PR-level
   verification to gather bounded evidence for synthesized or unresolved claims. Each ProCursor
   knowledge or symbol lookup records an explicit attempt status (`Succeeded`, `Empty`, or
   `Unavailable`) in the verification evidence bundle.
5. `ProviderReviewContextToolsBase` forwards review-time ProCursor calls through
   `IProCursorGateway` with repository or review-target context instead of querying ProCursor tables
   directly.
6. This keeps ProCursor as a bounded evidence dependency: verification can use repository-aware
   knowledge and symbol lookups without adding direct reviewing-to-ProCursor persistence coupling.
7. `DeterministicReviewFindingGate` remains retrieval-free. It consumes `VerificationOutcome`
   produced upstream rather than issuing its own ProCursor queries.

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
totals and gap-fill the newest uncaptured window from raw events.
