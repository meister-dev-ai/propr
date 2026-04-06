// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reviewing-owned persistence boundary for executing and finalizing review jobs.
/// </summary>
public interface IReviewJobExecutionStore
{
    ReviewJob? GetById(Guid id);

    IReadOnlyList<ReviewJob> GetPendingJobs();

    Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default);

    Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default);

    Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default);

    Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default);

    Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default);

    Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default);

    Task SetCancelledAsync(Guid id, CancellationToken ct = default);

    Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default);
}
