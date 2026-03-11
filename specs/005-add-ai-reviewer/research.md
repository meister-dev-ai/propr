# Research: Add AI Identity as Optional Reviewer (revised after clarification)

**Feature**: 005-add-ai-reviewer
**Date**: 2026-03-11
**Revision**: 2 — supersedes previous research.md; incorporates clarification that
`ReviewerId` moves from `CrawlConfiguration` to `Client`.

---

## Decision 1: Where to Store `ReviewerId`

**Decision**: New nullable `Guid? ReviewerId` column on the `clients` table, reflected on
`ClientRecord`. Removed from `CrawlConfigurationRecord` (column dropped via migration).

**Rationale**: One service account identity per client is the correct model — all ADO
operations for a given client run under the same identity. Storing it per crawl config was
an implementation artifact; the clarification session confirmed one value per client.

**Alternatives considered**:

| Option | Rejected because |
|--------|-----------------|
| Keep on `CrawlConfigurationRecord` | Violates clarified spec (FR-006); allows conflicting identities across configs for the same client. |
| New standalone `ClientReviewerConfiguration` entity | Over-engineering; nullable field on `ClientRecord` is sufficient and keeps the model flat. |
| Runtime-derived from `VssConnection.AuthorizedIdentity` | Requires an extra ADO round-trip per job; identity might differ from the configured reviewer in multi-tenant scenarios. |

---

## Decision 2: `CrawlConfigurationDto.ReviewerId` — Keep as Nullable Denormalised Field

**Decision**: `CrawlConfigurationDto` retains a `Guid? ReviewerId` property, but its value
is now sourced from the owning `Client` record, not from the `crawl_configurations` table.
The Postgres repository does a `JOIN` with `clients`; the in-memory repository looks up the
client store.

**Rationale**: `AdoAssignedPrFetcher` uses `config.ReviewerId` for `GitPullRequestSearchCriteria`
to filter open PRs assigned to the AI reviewer. Keeping `ReviewerId` on the DTO avoids
changing the `IAssignedPullRequestFetcher` interface and `PrCrawlService`. Making it nullable
allows graceful handling when a client has not yet configured a reviewer — the crawl service
skips that config with a warning log.

**Alternatives considered**:

| Option | Rejected because |
|--------|-----------------|
| Remove `ReviewerId` from DTO entirely | Requires changing `IAssignedPullRequestFetcher` signature — breaks Application layer contract unnecessarily. |
| Non-nullable `Guid ReviewerId` on DTO | Violates nullable model; forces a default Guid.Empty which could cause silent bugs. |

---

## Decision 3: How `ReviewOrchestrationService` Gets `ReviewerId`

**Decision**: Extend `IClientRegistry` with a new method
`Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct)`. The orchestration
service calls this at the start of `ProcessAsync`; if the result is `null`, the job is
immediately marked as `Failed` with a descriptive message (FR-001).

**Rationale**: `IClientRegistry` is the existing Application-layer abstraction for client
metadata. Adding one method is the minimal, least-surprise change. It keeps Infrastructure
details (DB / in-memory store) behind the interface.

**Alternatives considered**:

| Option | Rejected because |
|--------|-----------------|
| New `IClientRepository` interface | Introduces a new abstraction for a single method; unnecessary given `IClientRegistry` already exists. |
| Pass `ReviewerId` through `ReviewJob` | Would require a domain entity change and schema migration on the `review_jobs` table; `ReviewerId` is configuration, not job data. |
| Look up in Infrastructure directly | Violates Clean Architecture — Application services must not reference Infrastructure. |

---

## Decision 4: New API Endpoint for Setting `ReviewerId`

**Decision**: New `PUT /clients/{clientId}/reviewer-identity` endpoint (body:
`{ "reviewerId": "guid" }`, requires `X-Admin-Key`). Follows the identical pattern as
`PUT /clients/{clientId}/ado-credentials`.

**Rationale**: Setting the reviewer identity is a distinct administrative action, not a
property toggle like `IsActive`. A dedicated `PUT` sub-resource endpoint is idiomatic,
consistent with the existing `ado-credentials` pattern, and makes the intent explicit.

**Alternatives considered**:

| Option | Rejected because |
|--------|-----------------|
| Extend `PATCH /clients/{id}` | `PATCH` semantics blend active-flag toggling with credential storage; a dedicated sub-resource is cleaner. |
| `ReviewerDisplayName` resolve-and-store | Requires the organisation URL as an additional input and couples identity resolution to client management; easier to provide GUID directly. |

---

## Decision 5: `CreateCrawlConfigRequest.ReviewerDisplayName` — Remove

**Decision**: `ReviewerDisplayName` is removed from `CreateCrawlConfigRequest`. Crawl
configuration creation no longer resolves or stores a reviewer identity. The reviewer
identity is set separately via `PUT /clients/{clientId}/reviewer-identity`.

**Impact**: **Breaking API change** for existing consumers. Documented in contracts/.

**Rationale**: Coupling reviewer-identity resolution to crawl-config creation was an
architectural error now being corrected. Callers must set the reviewer identity at the
client level first.

**Alternatives considered**:

| Option | Rejected because |
|--------|-----------------|
| Keep `ReviewerDisplayName` optional in crawl config creation but use it to set client's `ReviewerId` | Confusing semantics — a crawl config operation silently mutates a client field. |
| Deprecate but keep the field | Leaves dead code; the resolution logic (IIdentityResolver call) would still run unnecessarily. |

---

## Decision 6: How `IAdoReviewerManager` Receives the Reviewer GUID

**Decision**: `IAdoReviewerManager.AddOptionalReviewerAsync` accepts an explicit `Guid reviewerId`
parameter. The orchestration service reads it from the client record and passes it in.

**Rationale**: The `IAdoReviewerManager` is a focused ADO-interaction interface; it should
not need to know about clients or configuration lookups. Explicit parameter is easier to
test and reason about.

---

## Decision 7: `CrawlConfigResponse` — Remove `ReviewerId`

**Decision**: `ReviewerId` is removed from the `CrawlConfigResponse` API response type.
`ReviewerId` is now returned on `ClientResponse` instead.

**Impact**: **Breaking API change** — consumers reading `reviewerId` from crawl config
responses must switch to reading it from the client response. Documented in contracts/.

---

## Decision 8: No New NuGet Packages

`GitHttpClient.CreatePullRequestReviewerAsync` and `VssConnection` are already in
`Microsoft.TeamFoundationServer.Client 20.269.0-preview`. `EF Core` migrations use the
existing `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0`. No new packages needed.
