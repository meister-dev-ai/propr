# Data Model: Add AI Identity as Optional Reviewer

**Feature**: 005-add-ai-reviewer
**Date**: 2026-03-11
**Revision**: 2 — supersedes previous data-model.md.

---

## Database Schema Changes

### `clients` table — Add column

```sql
ALTER TABLE clients ADD COLUMN reviewer_id UUID NULL;
```

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `reviewer_id` | `uuid` | YES | NULL | ADO identity GUID of the AI service account for this client. NULL = reviewer not configured; jobs will fail until set. |

### `crawl_configurations` table — Drop column

```sql
ALTER TABLE crawl_configurations DROP COLUMN reviewer_id;
```

The `reviewer_id` column is removed from `crawl_configurations`. EF Core unique index
`IX_CrawlConfigurations_ClientId_OrganizationUrl_ProjectId_ReviewerId` must also be
dropped and recreated without `reviewer_id`.

---

## EF Core Model Changes

### `ClientRecord` — Modified

Location: `src/MeisterProPR.Infrastructure/Data/Models/ClientRecord.cs`

**Add**:
```csharp
/// <summary>ADO identity GUID of the AI service account for this client. Null = not configured.</summary>
public Guid? ReviewerId { get; set; }
```

### `CrawlConfigurationRecord` — Modified

Location: `src/MeisterProPR.Infrastructure/Data/Models/CrawlConfigurationRecord.cs`

**Remove**:
```csharp
public Guid ReviewerId { get; set; }   // ← DELETED
```

---

## DTO Changes

### `CrawlConfigurationDto` — Modified

Location: `src/MeisterProPR.Application/DTOs/CrawlConfigurationDto.cs`

Change `Guid ReviewerId` → `Guid? ReviewerId` (nullable).
Value is now sourced from the owning client's `reviewer_id` via JOIN in the Postgres repository,
and from the in-memory client store in the in-memory repository.

```csharp
// Before
public sealed record CrawlConfigurationDto(
    Guid Id, Guid ClientId, string OrganizationUrl, string ProjectId,
    Guid ReviewerId,   // non-nullable
    int CrawlIntervalSeconds, bool IsActive, DateTimeOffset CreatedAt);

// After
public sealed record CrawlConfigurationDto(
    Guid Id, Guid ClientId, string OrganizationUrl, string ProjectId,
    Guid? ReviewerId,  // nullable — from client record
    int CrawlIntervalSeconds, bool IsActive, DateTimeOffset CreatedAt);
```

---

## Application Interface Changes

### `IClientRegistry` — Extended

Location: `src/MeisterProPR.Application/Interfaces/IClientRegistry.cs`

**Add method**:
```csharp
/// <summary>Returns the configured ADO reviewer identity GUID for the given client,
/// or null if not configured.</summary>
Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default);
```

### New: `IAdoReviewerManager`

Location: `src/MeisterProPR.Application/Interfaces/IAdoReviewerManager.cs`

```csharp
/// <summary>Adds the specified ADO identity as an optional reviewer on a pull request.</summary>
public interface IAdoReviewerManager
{
    /// <summary>
    ///     Adds <paramref name="reviewerId"/> as an optional (non-voting, non-required) reviewer
    ///     on the specified pull request. Idempotent. Throws on failure.
    /// </summary>
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

---

## Infrastructure Changes

### New: `AdoReviewerManager`

Location: `src/MeisterProPR.Infrastructure/AzureDevOps/AdoReviewerManager.cs`

Dependencies: `VssConnectionFactory`, `IClientAdoCredentialRepository`, `ILogger<AdoReviewerManager>`

Runtime behaviour:
1. Resolve per-client credentials (or fall back to global)
2. `GetConnectionAsync` (cached by factory)
3. `gitClient = connection.GetClient<GitHttpClient>()`
4. `await gitClient.CreatePullRequestReviewerAsync(`
   `new GitPullRequestReviewer { Vote = 0, IsRequired = false, Id = reviewerId.ToString() },`
   `repositoryId, pullRequestId, reviewerId.ToString(), projectId, ct)`
5. Log result; wrap steps 3–4 in `Activity` span with `pr.id`, `reviewer.id` attributes

### Updated: Postgres/in-memory `IClientRegistry` implementations

Both `DbClientRegistry` and `InMemoryClientRegistry` implement the new
`GetReviewerIdAsync(Guid clientId, ct)` method.

### Updated: `PostgresCrawlConfigurationRepository`

`GetAllActiveAsync` and `GetByClientAsync` JOIN with `clients` to populate
`CrawlConfigurationDto.ReviewerId` from `ClientRecord.ReviewerId`.

### Updated: `InMemoryCrawlConfigurationRepository`

`GetAllActiveAsync` and `GetByClientAsync` look up `ReviewerId` from the in-memory
client store (wherever `IClientRegistry` is backed).

---

## Orchestration Change

### `ReviewOrchestrationService` — Updated Step Sequence

New constructor parameter: `IAdoReviewerManager reviewerManager`
New dependency: `IClientRegistry clientRegistry` (for `GetReviewerIdAsync`)

```
ProcessAsync(ReviewJob job, CancellationToken ct):

1. reviewerId = await clientRegistry.GetReviewerIdAsync(job.ClientId, ct)
   → if null (or ClientId null): SetFailed("Reviewer identity not configured for client"); return

2. pr = await prFetcher.FetchAsync(...)
   → if pr.Status != Active: SetFailed("PR closed"); return

3. await reviewerManager.AddOptionalReviewerAsync(
       job.OrganizationUrl, job.ProjectId, job.RepositoryId,
       job.PullRequestId, reviewerId.Value, job.ClientId, ct)

4. result = await aiCore.ReviewAsync(pr, ct)

5. await commentPoster.PostAsync(...)

6. jobs.SetResult(job.Id, result)
```

Steps 3–5 propagate exceptions into the existing outer `catch`, which calls `SetFailed`.

---

## Crawl Service Change

### `PrCrawlService` / `AdoPrCrawlerWorker`

If `config.ReviewerId` is null (client has no reviewer configured), skip the crawl config
and emit a structured warning log. Do not attempt PR fetching.

---

## EF Core Migration

New migration class: `AddReviewerIdToClients_RemoveFromCrawlConfigs`

Operations:
1. `migrationBuilder.AddColumn<Guid>("reviewer_id", "clients", nullable: true)`
2. Drop existing unique index on `crawl_configurations` that includes `reviewer_id`
3. `migrationBuilder.DropColumn("reviewer_id", "crawl_configurations")`
4. Recreate unique index on `crawl_configurations (client_id, organization_url, project_id)`
   (without `reviewer_id`)
