# Implementation Plan: Add AI Identity as Optional Reviewer

**Branch**: `005-add-ai-reviewer` | **Date**: 2026-03-11 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/005-add-ai-reviewer/spec.md`

## Summary

`ReviewerId` (the ADO identity GUID of the AI service account) moves from
`CrawlConfigurationRecord` to `ClientRecord` — one value per client, set via a new
`PUT /clients/{id}/reviewer-identity` admin endpoint. Before a review job posts any
comments the orchestration service adds that identity as an optional reviewer on the PR.
If the client has no `ReviewerId` configured, or if the ADO call fails, the job is
immediately marked as failed and no comments are posted.

Breaking API changes: `reviewerId` removed from `CrawlConfigResponse` and
`CreateCrawlConfigRequest`; both removed fields are now on the client resource.
One new EF Core migration.

## Technical Context

**Language/Version**: C# 13 / .NET 10, TFM `net10.0`
**Primary Dependencies**: ASP.NET Core MVC, EF Core 10.0.3, Npgsql 10.0.0, `Microsoft.TeamFoundationServer.Client 20.269.0-preview`, `Azure.Identity 1.14.2` — all existing; no new packages
**Storage**: PostgreSQL 17 — one column added to `clients`, one column dropped from `crawl_configurations`; one new EF Core migration
**Testing**: xUnit + NSubstitute; `WebApplicationFactory<Program>` for integration tests
**Target Platform**: Linux rootless container (`mcr.microsoft.com/dotnet/aspnet:10.0`)
**Project Type**: Web service (ASP.NET Core)
**Performance Goals**: One extra DB read per job (reviewer GUID lookup); well within 120 s budget
**Constraints**: No new env vars; no new NuGet packages; `openapi.json` must be regenerated and committed

## Constitution Check

- [x] **I. API-Contract-First** — Two breaking changes to existing endpoints (crawl config response + request) plus one new endpoint. `openapi.json` MUST be regenerated and committed. See [contracts/openapi-delta.md](contracts/openapi-delta.md). No version bump — feature branch will coordinate with extension update.
- [x] **II. Test-First** — `[TEST]` tasks lead in `tasks.md`. Red → Green → Refactor per task.
- [x] **III. Container-First** — No Windows-specific APIs. No new env vars. `/healthz` unaffected.
- [x] **IV. Clean Architecture** — `IAdoReviewerManager` and `IClientRegistry.GetReviewerIdAsync` in Application. `AdoReviewerManager` in Infrastructure. `ReviewOrchestrationService` depends only on Application interfaces. Controller extension in Api. No cross-layer violations.
- [x] **V. Security** — `ReviewerId` is an identity GUID, not a secret; safe to log. `X-Admin-Key` guards the new endpoint. No ADO tokens stored or returned.
- [x] **VI. Job Reliability** — Null `ReviewerId` → immediate `SetFailed` before any processing. ADO failure → caught by existing outer catch → `SetFailed`. Status lifecycle unchanged.
- [x] **VII. Observability** — Structured log on null `ReviewerId` rejection. `Activity` span on `CreatePullRequestReviewerAsync` call. Existing log on job failure covers reviewer-addition errors.

## Project Structure

### Documentation (this feature)

```text
specs/005-add-ai-reviewer/
├── plan.md              # This file
├── research.md          # Phase 0 output (revised)
├── data-model.md        # Phase 1 output (revised)
├── quickstart.md        # Phase 1 output (revised)
├── contracts/
│   └── openapi-delta.md # Phase 1 output (revised — breaking changes documented)
└── tasks.md             # Phase 2 output (/speckit.tasks)
```

### Source Code

```text
src/
├── MeisterProPR.Domain/
│   └── (no changes)
│
├── MeisterProPR.Application/
│   ├── DTOs/
│   │   └── CrawlConfigurationDto.cs          [MODIFIED — ReviewerId: Guid → Guid?]
│   ├── Interfaces/
│   │   ├── IClientRegistry.cs                [MODIFIED — add GetReviewerIdAsync]
│   │   └── IAdoReviewerManager.cs            [NEW]
│   └── Services/
│       └── ReviewOrchestrationService.cs     [MODIFIED — null-check + reviewer step]
│
├── MeisterProPR.Infrastructure/
│   ├── AzureDevOps/
│   │   └── AdoReviewerManager.cs             [NEW]
│   ├── Data/
│   │   ├── Models/
│   │   │   ├── ClientRecord.cs               [MODIFIED — add Guid? ReviewerId]
│   │   │   └── CrawlConfigurationRecord.cs   [MODIFIED — remove Guid ReviewerId]
│   │   └── MeisterProPRDbContext.cs          [check — unique index update if configured here]
│   ├── Migrations/
│   │   └── <timestamp>_AddReviewerIdToClients_RemoveFromCrawlConfigs.cs  [NEW]
│   ├── Configuration/
│   │   └── EnvVarClientRegistry.cs           [MODIFIED — implement GetReviewerIdAsync (returns null)]
│   └── Repositories/
│       ├── PostgresClientRegistry.cs         [MODIFIED — implement GetReviewerIdAsync]
│       └── PostgresCrawlConfigurationRepository.cs  [MODIFIED — JOIN client for ReviewerId]
│
└── MeisterProPR.Api/
    ├── Controllers/
    │   └── ClientsController.cs              [MODIFIED — new endpoint, updated response/request types]
    └── Program.cs                            [MODIFIED — register AdoReviewerManager]

tests/
├── MeisterProPR.Application.Tests/
│   └── Services/
│       └── ReviewOrchestrationServiceTests.cs  [MODIFIED — null ReviewerId + reviewer scenarios]
├── MeisterProPR.Infrastructure.Tests/
│   └── AzureDevOps/
│       └── AdoReviewerManagerTests.cs          [NEW]
└── MeisterProPR.Api.Tests/
    └── Controllers/
        └── ClientsControllerTests.cs           [MODIFIED — new endpoint scenarios]
```

**Structure Decision**: Single Clean Architecture layout, no new projects.

## Complexity Tracking

> No constitution violations — no entries required.

---

## Implementation Detail

### `IClientRegistry` — New Method

```csharp
/// <summary>Returns the configured ADO reviewer identity GUID for the given client,
/// or null if not configured or client not found.</summary>
Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default);
```

### `IAdoReviewerManager` — New Interface

```csharp
public interface IAdoReviewerManager
{
    Task AddOptionalReviewerAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);
}
```

### `AdoReviewerManager` — New Implementation

```text
Constructor: AdoReviewerManager(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoReviewerManager> logger)

AddOptionalReviewerAsync:
1. Resolve per-client or global credentials
2. GetConnectionAsync (cached)
3. gitClient = connection.GetClient<GitHttpClient>()
4. Wrap in ActivitySource span (reviewer.id, pr.id attributes)
5. await gitClient.CreatePullRequestReviewerAsync(
       new GitPullRequestReviewer { Vote = 0, IsRequired = false, Id = reviewerId.ToString() },
       repositoryId, pullRequestId, reviewerId.ToString(), projectId, ct)
6. Log success
```

### `ReviewOrchestrationService` — Updated `ProcessAsync`

New constructor params: `IAdoReviewerManager reviewerManager`, `IClientRegistry clientRegistry`

```text
ProcessAsync:
1. if job.ClientId is null → SetFailed("No client associated with job"); return
   reviewerId = await clientRegistry.GetReviewerIdAsync(job.ClientId.Value, ct)
   if reviewerId is null → SetFailed("Reviewer identity not configured for client {ClientId}"); return

2. pr = await prFetcher.FetchAsync(...)
   if pr.Status != Active → SetFailed("PR closed/abandoned"); return

3. await reviewerManager.AddOptionalReviewerAsync(
       job.OrganizationUrl, job.ProjectId, job.RepositoryId,
       job.PullRequestId, reviewerId.Value, job.ClientId, ct)
   [any exception → outer catch → SetFailed]

4. result = await aiCore.ReviewAsync(pr, ct)

5. await commentPoster.PostAsync(...)

6. jobs.SetResult(job.Id, result)
```

### `ClientsController` — Changes

1. `ClientResponse` record gains `Guid? ReviewerId`
2. `ToClientResponse()` maps `client.ReviewerId`
3. `CrawlConfigResponse` record: `Guid ReviewerId` removed
4. `CreateCrawlConfigRequest` record: `string ReviewerDisplayName` removed; identity-resolver call in `PostCrawlConfig` action removed
5. `PatchClientRequest` record: unchanged (reviewer has its own dedicated endpoint)
6. New action `PutReviewerIdentity`:

```text
PUT /clients/{clientId}/reviewer-identity
[HttpPut("clients/{clientId:guid}/reviewer-identity")]
Body: SetReviewerIdentityRequest(Guid ReviewerId)
- Admin-key guard
- Validate ReviewerId != Guid.Empty
- Find client; 404 if not found
- client.ReviewerId = request.ReviewerId; SaveChangesAsync
- Return 204 No Content
```

### `Program.cs`

```csharp
builder.Services.AddSingleton<IAdoReviewerManager, AdoReviewerManager>();
```

### `PrCrawlService` / Crawler Worker

Where configs are iterated: if `config.ReviewerId is null`, log a structured warning and
skip that config — do not attempt PR fetching for a client without a reviewer identity.

### Repository Updates

**`PostgresCrawlConfigurationRepository`**: queries that return `CrawlConfigurationDto`
do a `LEFT JOIN` (or `Include` navigation) with `clients` to populate `ReviewerId` from
`ClientRecord.ReviewerId`.

**`EnvVarClientRegistry`**: `GetReviewerIdAsync` always returns `null` — no DB storage
in env-var mode. Crawl-based jobs are not triggered in this mode (no persistent crawl
configs), so the null return is safe.
