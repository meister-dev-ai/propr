# Configuration And Crawling

This page covers the admin configuration workflow that defines what the system may review, and the
background crawler workflow that turns saved configuration into queued review jobs.

## Guided Azure DevOps Configuration

Guided Azure DevOps onboarding separates credential ownership from admin selections. Client
credentials authorize the backend to talk to Azure DevOps. Client-scoped organization-scope
records define which Azure DevOps organizations administrators may choose in guided ProCursor and
crawl-config flows.

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

All downstream project, repository, wiki, branch, and crawl-filter choices are resolved through
discovery endpoints and then revalidated again at save time. Compatibility remains for legacy
callers that still send raw `organizationUrl` and repository identifiers, but the guided path is
the primary admin boundary.

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
