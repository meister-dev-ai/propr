# Configuration And Crawling

This page describes the admin configuration workflow that defines reviewable scope and the
background crawler workflow that turns saved configuration into queued review jobs. Azure DevOps,
GitHub, GitLab, and Forgejo-family hosts use shared provider-neutral connection, scope,
repository, review, revision, and webhook concepts for manual review, webhook activation, and
operational visibility. Guided Azure DevOps discovery is available through the same provider model.

## Provider-Neutral Runtime Guardrails

- Provider onboarding, manual review intake, webhook ingress, and provider-scoped observability run
    through one shared provider-neutral model instead of branching the application workflow per
    provider family.
- Azure DevOps provides guided discovery for project, source, and branch selection. Credentials,
    organization scopes, and reviewer setup are resolved from the same provider connections and
    provider scopes used by the other supported SCM families.
- Provider connections own secrets and host configuration; provider scopes define the usable
    administrative boundary inside each connection.
- Repository, review, revision, thread, comment, and webhook concepts are normalized so downstream
    deduplication, thread memory, observability, and audit paths stay shared across provider families.
- Provider readiness is evaluated as a separate read model. Verification answers whether onboarding
    checks passed; readiness answers whether the connection is configured, onboarding-ready,
    degraded, or workflow-complete for the selected provider family and host variant.

## Guided Azure DevOps Configuration Through Provider Connections

Azure DevOps is administered through the shared provider-management flow. Administrators create or
update an Azure DevOps provider connection in the Providers tab, add one or more organization
scopes to that connection, and then use the same provider-scoped reviewer identity and discovery
surfaces as the other supported SCM families.

The provider-connection model does not use client-level System Azure DevOps settings. Configure
Azure DevOps in this order:

1. Add the Azure DevOps provider connection.
2. Add the organization scope on that connection.
3. Confirm or update the reviewer identity for that connection.
4. Re-save crawl, ProCursor, or webhook configuration against the recreated provider-backed scope.

```mermaid
sequenceDiagram
    actor Admin
    participant UI as Admin UI
    participant PCON as ClientProviderConnectionsController
    participant PSC as ClientProviderScopesController
    participant PRI as ClientReviewerIdentitiesController
    participant DR as AdoDiscoveryController
    participant DS as IAdoDiscoveryService
    participant PC as ProCursorKnowledgeSourcesController
    participant PG as IProCursorGateway
    participant CFG as AdminCrawlConfigsController

    Admin->>UI: Add Azure DevOps provider connection
    UI->>PCON: POST /clients/{id}/provider-connections
    Admin->>UI: Add Azure DevOps organization scope
    UI->>PSC: POST /clients/{id}/provider-connections/{connectionId}/scopes
    Admin->>UI: Set reviewer identity
    UI->>PRI: PUT /clients/{id}/provider-connections/{connectionId}/reviewer-identity
    UI->>DR: GET /admin/clients/{id}/ado/discovery/projects
    DR->>DS: Resolve live projects for provider-backed organizationScopeId
    UI->>DR: GET discovery sources / branches / crawl-filters
    DR->>DS: Resolve live source metadata
    UI->>PC: POST guided ProCursor source
    PC->>PG: CreateSourceAsync(... organizationScopeId, canonicalSourceRef ...)
    PG->>DS: Revalidate project/source/branch
    UI->>CFG: POST guided crawl configuration
    CFG->>DS: Revalidate selected crawl filters
    CFG-->>UI: 201 or actionable 4xx/409 drift error
```

All downstream project, repository, wiki, branch, and crawl-filter choices are resolved through
discovery endpoints and revalidated at save time. The durable admin boundary is the provider
connection and its scopes, not a client-level System Azure DevOps record.

## Webhook Configuration And Delivery History

Webhook configuration management is a sibling flow to crawl configuration management. Each webhook
configuration belongs to one client, stores a protected secret, owns a unique public listener path,
and persists repository and branch scope on the server. Azure DevOps configurations use the same
provider-backed organization selection and listener route shape as the other supported providers:
`/webhooks/v1/providers/{provider}/{pathKey}`. Those saved rules remain authoritative even when a
provider also applies its own upstream filters.

```mermaid
sequenceDiagram
    actor Admin
    participant UI as Admin UI
    participant DR as AdoDiscoveryController
    participant DS as IAdoDiscoveryService
    participant WH as AdminWebhookConfigsController
    participant WR as IWebhookConfigurationRepository
    participant DL as IWebhookDeliveryLogRepository

    Admin->>UI: Open client webhook tab
    UI->>DR: GET discovery projects / crawl-filters (Azure DevOps)
    DR->>DS: Resolve live ADO metadata
    Admin->>UI: Save webhook configuration
    UI->>WH: POST /admin/webhook-configurations
    WH->>DS: Revalidate selected scope (Azure DevOps)
    WH->>WR: Persist path key, protected secret, and repo filters
    WH-->>UI: Listener URL + one-time generated secret
    UI->>WH: GET /admin/webhook-configurations/{id}/deliveries
    WH->>DL: Load recent per-config delivery history
    WH-->>UI: Accepted / ignored / rejected / failed entries
```

Delivery-history entries are durable per configuration and deliberately sanitized. They capture the
received timestamp, normalized event type, response status, final outcome, provider-specific failure
category, PR context when present, and downstream action summaries without storing auth material or
raw secrets.

## Public Webhook Intake

The public provider receiver sits beside the crawler rather than replacing it. GitHub, GitLab,
Forgejo-family, and Azure DevOps deliveries arrive through
`/webhooks/v1/providers/{provider}/{pathKey}`. Deliveries are matched by opaque path key,
verified with the provider-specific ingress service, checked against the saved repository and
branch scope, and handed to the shared pull-request synchronization service so lifecycle handling,
review-intake deduplication, scan updates, and reviewer-thread memory transitions are decided from
one downstream path.

```mermaid
sequenceDiagram
    participant PRV as SCM Provider Web Hook
    participant RX as ProviderWebhookReceiverController
    participant HD as HandleProviderWebhookDeliveryHandler
    participant WR as IWebhookConfigurationRepository
    participant ING as IWebhookIngressService
    participant DL as IWebhookDeliveryLogRepository
    participant SYNC as IPullRequestSynchronizationService
    participant SCAN as IReviewPrScanRepository
    participant TM as IThreadMemoryService
    participant JR as IJobRepository

    PRV->>RX: POST /webhooks/v1/providers/{provider}/{pathKey}
    RX->>HD: HandleAsync(command)
    HD->>WR: Resolve active configuration by path key
    HD->>ING: Verify + parse provider payload
    HD->>HD: Enforce saved repository / branch scope
    HD->>SYNC: SynchronizeAsync(source=webhook, summaryLabel, PR context)
    SYNC->>SCAN: Load current review watermark and reviewer thread state
    SYNC->>TM: Emit resolved/reopened thread-memory transitions when status changed
    SYNC->>JR: Queue review job, skip duplicate, or cancel active jobs
    HD->>DL: Persist accepted / ignored / rejected / failed outcome
    HD-->>RX: Small status payload
    RX-->>PRV: Fast acknowledgement
```

The crawler and webhook listener can target the same client or repository. Webhooks reduce the time
to first action, while the crawler provides periodic discovery and fallback coverage for
provider-backed configurations. Both sources feed the same downstream synchronization path before
durable action is taken, which prevents provider-specific shortcuts from diverging from shared
lifecycle, thread-memory, audit, or retention behavior.

## PR Crawler Flow

The crawler finds new pull requests automatically. It periodically scans active crawl
configurations, resolves matching PRs for the configured reviewer, and queues review jobs only when
no active job already exists for the same PR state.

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

The crawler operates against crawl configurations that may reference all client ProCursor sources
or a selected subset. That selection is durable and does not depend on reading the latest admin
configuration during review execution.

Each crawl or webhook activation path can also snapshot an optional review temperature. Review
execution uses the job-scoped value, so later admin edits do not retroactively change in-flight
work.

## Shared Pull-Request Synchronization

`IPullRequestSynchronizationService` is the convergence point between webhook-triggered and
crawler-triggered activity. It receives source-neutral PR activation context, resolves the latest
iteration when necessary, runs the reviewer-thread memory state machine against the current
`ReviewPrScan`, then decides whether to enqueue review intake, suppress duplicate work, or cancel
active jobs for closed PRs.

```mermaid
sequenceDiagram
    participant SRC as Crawl or Webhook
    participant SYNC as PullRequestSynchronizationService
    participant SCAN as IReviewPrScanRepository
    participant TS as IReviewerThreadStatusFetcher
    participant TM as IThreadMemoryService
    participant JR as IJobRepository

    SRC->>SYNC: SynchronizeAsync(request)
    SYNC->>SCAN: Load scan state for client/repo/PR
    alt Reviewer identity and scan available
        SYNC->>TS: Fetch current reviewer-owned thread statuses
        SYNC->>TM: Store/remove thread memory for resolved/reopened transitions
        SYNC->>SCAN: Persist updated last-seen statuses
    end
    alt PR closed or abandoned
        SYNC->>JR: Cancel active jobs for the PR
    else Active PR with no duplicate/no-op condition
        SYNC->>JR: Add pending review job with snapshotted source scope
    end
    SYNC-->>SRC: Outcome with review/lifecycle decisions and action summaries
```

This shared seam is also where synchronization-level telemetry is emitted. Observability tracks
activation source, PR status, review decision, and lifecycle decision for each pass, which makes it
possible to compare webhook- and crawler-driven behavior using the same tracing and metric series.

## Crawl Source Scope Snapshotting

When a crawl configuration uses `selectedSources`, the crawler snapshots the chosen source list
onto the queued `ReviewJob`. Review execution later consumes the snapshot so an admin edit made
after queue time cannot silently change the knowledge scope of in-flight work.

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

This preserves queue-time intent and avoids hidden behavior changes between admin saves and worker
execution.
