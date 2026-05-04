# Vertical Slice Modules

## Purpose

This document is the durable ownership map for the backend modular-monolith migration. It defines the module catalog, what each module owns, and which seams remain shared support rather than business modules.

## Module Catalog

| Module | Scope | Module Entry Point | Primary Contracts |
|--------|-------|---------------------|-------------------|
| Reviewing | Review intake, execution orchestration, diagnostics, and thread memory | `AddReviewingModule()` | `IJobRepository`, `IProtocolRecorder`, `IFileByFileReviewOrchestrator`, `IThreadMemoryService` |
| Crawling | Crawl configuration, ADO discovery, and periodic PR scan execution | `AddCrawlingModule()` | `ICrawlConfigurationRepository`, `IReviewPrScanRepository`, `IPrCrawlService`, `IAdoDiscoveryService` |
| Clients | Client administration and AI connection ownership | `AddClientsModule()` | `IClientRegistry`, `IClientAdminService`, `IAiConnectionRepository` |
| IdentityAndAccess | Login, PATs, refresh tokens, password hashing, and bootstrap | `AddIdentityAndAccessModule()` | `IUserRepository`, `IRefreshTokenRepository`, `IUserPatRepository`, `IJwtTokenService` |
| Mentions | Mention scan and AI-assisted replies | `AddMentionsModule()` | `IMentionScanRepository`, `IMentionReplyJobRepository`, `IMentionScanService`, `IMentionReplyService` |
| PromptCustomization | Prompt override management | `AddPromptCustomizationModule()` | `IPromptOverrideRepository`, `IPromptOverrideService` |
| UsageReporting | Client and ProCursor token usage reporting | `AddUsageReportingModule()` | `IClientTokenUsageRepository`, `IProCursorTokenUsageRecorder`, `IProCursorTokenUsageReadRepository` |
| ProCursor | Knowledge indexing, graph analysis, and query support | `AddProCursorModule()` | `IProCursorGateway`, `IProCursorKnowledgeSourceRepository`, `IProCursorIndexJobRepository` |

## Shared Support Groups

These concerns remain shared support because they provide technical capabilities rather than business workflows.

| Support Group | Responsibilities |
|---------------|------------------|
| Persistence plumbing | `MeisterProPRDbContext`, EF Core provider configuration, db-context factory |
| Azure DevOps transport | Token validation, ADO API clients, identity resolution, PR fetchers, thread clients |
| AI runtime | Chat client factory, embedding generator factory, evaluator client registration, AI option binding |
| Security support | Secret protection codec and shared credential resolution helpers |
| Startup/runtime support | Host options, OpenTelemetry bootstrapping, data protection, health checks, worker hosting |

## Dependency Rules

- `Program.cs` may compose shared support and module entry points, but it should not register feature-internal repositories or services one by one.
- Module roots may depend on shared support services and public contracts from other modules.
- Shared support must not absorb feature-owned repositories, stores, or orchestration services.
- Cross-module calls should use public interfaces or DTOs rather than implementation types.
- DB-backed module registrations key directly off `DB_CONNECTION_STRING`, including in `Testing`; test hosts that need an isolated graph should avoid inheriting a PostgreSQL connection string or override the affected services explicitly.
- Feature work follows matching `Features/` paths without changing the module catalog unless the docs and guardrail tests are updated together.

## Review Checklist Alignment

Use the wave-review checklist in `specs/034-vertical-slice-migration/checklists/wave-review.md` to confirm that each new slice follows this ownership map before a legacy seam is removed.
