# Data Model: MVP Backend — Local AI Code Review

**Branch**: `001-mvp-backend` | **Phase**: 1 — Design | **Date**: 2026-03-03

---

## Domain Layer (`MeisterProPR.Domain`)

All entities and value objects below are pure C# — zero external NuGet dependencies.

---

### Entity: `ReviewJob`

The root aggregate for a single AI review request.

| Field             | Type              | Nullable | Notes                                                        |
|-------------------|-------------------|----------|--------------------------------------------------------------|
| `Id`              | `Guid`            | No       | Generated on creation (`Guid.NewGuid()`)                     |
| `Status`          | `JobStatus`       | No       | State machine (see transitions below)                        |
| `ClientKey`       | `string`          | No       | Key of the client who submitted — used to scope list queries |
| `OrganizationUrl` | `string`          | No       | ADO organisation URL                                         |
| `ProjectId`       | `string`          | No       | ADO project ID or name                                       |
| `RepositoryId`    | `string`          | No       | ADO repository ID (GUID or name)                             |
| `PullRequestId`   | `int`             | No       | ADO pull request numeric ID                                  |
| `IterationId`     | `int`             | No       | PR iteration (change version) to review                      |
| `SubmittedAt`     | `DateTimeOffset`  | No       | UTC timestamp when job was accepted                          |
| `CompletedAt`     | `DateTimeOffset?` | Yes      | UTC timestamp when status became `Completed` or `Failed`     |
| `Result`          | `ReviewResult?`   | Yes      | Set when `Status == Completed`                               |
| `ErrorMessage`    | `string?`         | Yes      | Human-readable error when `Status == Failed`                 |

**Status Transitions** (valid transitions only — all others are programming errors):

```
Pending ──► Processing ──► Completed
                       └──► Failed
```

No other transitions are permitted. `Pending → Failed` (e.g. immediate rejection)
is also not valid — the job must pass through `Processing` first.

**Idempotency key**: composite `(OrganizationUrl, ProjectId, RepositoryId, PullRequestId, IterationId)`.
A non-`Failed` job with this key already existing returns the existing `Id` (FR-012).

**Validation rules**:

- `Id` must not be `Guid.Empty`
- `OrganizationUrl` must be a non-empty URI string
- `ProjectId`, `RepositoryId` must be non-empty strings
- `PullRequestId`, `IterationId` must be ≥ 1

---

### Value Object: `PullRequest`

Transient domain representation of an ADO pull request for a given iteration.
Never persisted — constructed by Infrastructure, consumed by the review pipeline,
discarded after the job completes.

| Field             | Type                         | Nullable | Notes                                                        |
|-------------------|------------------------------|----------|--------------------------------------------------------------|
| `OrganizationUrl` | `string`                     | No       |                                                              |
| `ProjectId`       | `string`                     | No       |                                                              |
| `RepositoryId`    | `string`                     | No       |                                                              |
| `PullRequestId`   | `int`                        | No       |                                                              |
| `IterationId`     | `int`                        | No       |                                                              |
| `Title`           | `string`                     | No       | PR title from ADO                                            |
| `Description`     | `string?`                    | Yes      | PR description, may be null                                  |
| `SourceBranch`    | `string`                     | No       | Source ref name                                              |
| `TargetBranch`    | `string`                     | No       | Target ref name                                              |
| `ChangedFiles`    | `IReadOnlyList<ChangedFile>` | No       | Ordered list of file changes; empty list if no files changed |

---

### Value Object: `ChangedFile`

A single file modified in the PR iteration, with full content and diff text.

| Field         | Type         | Nullable | Notes                                                                      |
|---------------|--------------|----------|----------------------------------------------------------------------------|
| `Path`        | `string`     | No       | Repository-relative path (e.g. `/src/Foo/Bar.cs`)                          |
| `ChangeType`  | `ChangeType` | No       | `Add`, `Edit`, or `Delete`                                                 |
| `FullContent` | `string`     | No       | Full file text at head commit; `""` for deleted files                      |
| `UnifiedDiff` | `string`     | No       | Unified diff text (generated by DiffPlex); `""` for pure adds with no base |

Both `FullContent` and `UnifiedDiff` are provided to the AI to maximise
review quality and enable accurate line-number attribution (FR-003).

---

### Value Object: `ReviewResult`

The output produced by the AI review.

| Field      | Type                           | Nullable | Notes                                    |
|------------|--------------------------------|----------|------------------------------------------|
| `Summary`  | `string`                       | No       | Overall narrative review from the AI     |
| `Comments` | `IReadOnlyList<ReviewComment>` | No       | Zero or more findings; may be empty list |

---

### Value Object: `ReviewComment`

A single AI-generated finding anchored to a file and/or line.

| Field        | Type              | Nullable | Notes                                                |
|--------------|-------------------|----------|------------------------------------------------------|
| `FilePath`   | `string?`         | Yes      | Relative file path; `null` → PR-level comment        |
| `LineNumber` | `int?`            | Yes      | 1-based line in the file; `null` → not line-specific |
| `Severity`   | `CommentSeverity` | No       | `Info`, `Warning`, `Error`, or `Suggestion`          |
| `Message`    | `string`          | No       | Actionable description                               |

**Posting rules** (FR-013):

- `FilePath` ≠ null AND `LineNumber` ≠ null → inline ADO thread anchored to file + line
- `FilePath` ≠ null AND `LineNumber` == null → inline ADO thread anchored to file only
- `FilePath` == null → PR-level (general) ADO thread

---

### Value Object: `ClientRegistration`

An authorised client identity. For MVP, sourced exclusively from env var config.

| Field | Type     | Notes                                        |
|-------|----------|----------------------------------------------|
| `Key` | `string` | The raw client key string; unique identifier |

---

### Domain Interface: `IAiReviewCore`

Defined in Domain so the domain's review orchestration owns the contract
(per FR-004). Accepts only domain entities; returns only domain entities — no
AI SDK types cross this boundary.

```csharp
namespace MeisterProPR.Domain.Interfaces;

public interface IAiReviewCore
{
    Task<ReviewResult> ReviewAsync(
        PullRequest pullRequest,
        CancellationToken cancellationToken = default);
}
```

---

### Enumerations

#### `JobStatus`

```csharp
public enum JobStatus { Pending, Processing, Completed, Failed }
```

#### `ChangeType`

```csharp
public enum ChangeType { Add, Edit, Delete }
```

#### `CommentSeverity`

```csharp
public enum CommentSeverity { Info, Warning, Error, Suggestion }
```

---

## Application Layer (`MeisterProPR.Application`)

Interfaces defined here so business logic is independent of infrastructure.

---

### Interface: `IJobRepository`

```csharp
public interface IJobRepository
{
    /// <summary>Returns the first non-Failed job for the given PR iteration, or null.</summary>
    ReviewJob? FindActiveJob(
        string organizationUrl, string projectId, string repositoryId,
        int pullRequestId, int iterationId);

    void Add(ReviewJob job);

    ReviewJob? GetById(Guid id);

    /// <summary>All jobs for a client key, newest first.</summary>
    IReadOnlyList<ReviewJob> GetAllForClient(string clientKey);

    /// <summary>Atomic compare-and-swap on Status. Returns false if current status ≠ from.</summary>
    bool TryTransition(Guid id, JobStatus from, JobStatus to);

    void SetResult(Guid id, ReviewResult result);

    void SetFailed(Guid id, string errorMessage);
}
```

---

### Interface: `IClientRegistry`

```csharp
public interface IClientRegistry
{
    bool IsValidKey(string clientKey);
}
```

---

### Interface: `IPullRequestFetcher`

```csharp
public interface IPullRequestFetcher
{
    Task<PullRequest> FetchAsync(
        string organizationUrl, string projectId, string repositoryId,
        int pullRequestId, int iterationId,
        CancellationToken cancellationToken = default);
}
```

---

### Interface: `IAdoCommentPoster`

```csharp
public interface IAdoCommentPoster
{
    Task PostAsync(
        string organizationUrl, string projectId, string repositoryId,
        int pullRequestId, int iterationId,
        ReviewResult result,
        CancellationToken cancellationToken = default);
}
```

---

### Interface: `IAdoTokenValidator`

```csharp
public interface IAdoTokenValidator
{
    /// <summary>
    /// Returns true if the ADO token is valid for the configured organisation.
    /// Used solely for identity verification (FR-015) — never for ADO API operations.
    /// </summary>
    Task<bool> IsValidAsync(string adoToken, CancellationToken ct = default);
}
```

---

### Service: `ReviewOrchestrationService`

The core business logic service — called by `ReviewJobWorker` for each
`Processing` job. Wires together ADO fetching, AI review, and comment posting.

```csharp
public sealed class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IAiReviewCore aiCore,
    IAdoCommentPoster commentPoster,
    ILogger<ReviewOrchestrationService> logger)
{
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct);
}
```

**ProcessAsync algorithm**:

1. `prFetcher.FetchAsync(...)` → `PullRequest`
2. `aiCore.ReviewAsync(pullRequest, ct)` → `ReviewResult`
3. `commentPoster.PostAsync(...)` with result
4. `jobs.SetResult(job.Id, result)` + transition to `Completed`
5. On any exception: `jobs.SetFailed(job.Id, ex.Message)` + log error

---

## Infrastructure Layer (`MeisterProPR.Infrastructure`)

Concrete implementations of all Application interfaces.

| Interface             | Implementation          | Storage / SDK                                   |
|-----------------------|-------------------------|-------------------------------------------------|
| `IJobRepository`      | `InMemoryJobRepository` | `ConcurrentDictionary<Guid, ReviewJob>`         |
| `IClientRegistry`     | `EnvVarClientRegistry`  | `MEISTER_CLIENT_KEYS` env var (comma-separated) |
| `IPullRequestFetcher` | `AdoPullRequestFetcher` | `GitHttpClient` (ADO SDK)                       |
| `IAdoCommentPoster`   | `AdoCommentPoster`      | `GitHttpClient` (ADO SDK)                       |
| `IAdoTokenValidator`  | `AdoTokenValidator`     | `IHttpClientFactory` → named `HttpClient`       |
| `IAiReviewCore`       | `AgentAiReviewCore`     | `IChatClient` (Microsoft.Extensions.AI)         |

---

## OpenAPI Schema Mapping

The existing `openapi.json` at the repository root maps to the data model as follows:

| OpenAPI Schema             | Domain / DTO                                      |
|----------------------------|---------------------------------------------------|
| `ReviewRequest`            | Maps to `ReviewJob` creation parameters           |
| `ReviewJob` (202 response) | Wrapper around `ReviewJob.Id`                     |
| `ReviewJobStatus` enum     | `JobStatus` enum (lowercase string serialisation) |
| `ReviewListItem`           | `ReviewJob` summary projection                    |
| `ReviewStatusResponse`     | `ReviewJob` + `ReviewResult?`                     |
| `ReviewResult`             | `ReviewResult`                                    |
| `ReviewComment`            | `ReviewComment`                                   |

**Note**: The existing `openapi.json` describes `X-Ado-Token` as "used by the
backend to fetch PR data and post review comments." This description is incorrect
per FR-015 — it must be corrected to "used solely to verify the requesting user
is an authenticated ADO organisation member; never forwarded or used for ADO API
operations." See `contracts/openapi-notes.md`.
