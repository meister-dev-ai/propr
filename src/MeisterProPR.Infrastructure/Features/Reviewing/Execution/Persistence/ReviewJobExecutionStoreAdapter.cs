// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Adapts the legacy job repository onto the Reviewing execution boundary.
/// </summary>
public sealed class ReviewJobExecutionStoreAdapter(IJobRepository inner) : IReviewJobExecutionStore
{
    public ReviewJob? GetById(Guid id)
    {
        return inner.GetById(id);
    }

    public IReadOnlyList<ReviewJob> GetPendingJobs()
    {
        return inner.GetPendingJobs();
    }

    public Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(
        TimeSpan threshold,
        CancellationToken ct = default)
    {
        return inner.GetStuckProcessingJobsAsync(threshold, ct);
    }

    public Task<int> CountProcessingJobsAsync(CancellationToken ct = default)
    {
        return inner.CountProcessingJobsAsync(ct);
    }

    public Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default)
    {
        return inner.TryTransitionAsync(id, from, to, ct);
    }

    public Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default)
    {
        return inner.UpdateRetryCountAsync(id, retryCount, ct);
    }

    public Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
    {
        return inner.SetFailedAsync(id, errorMessage, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        return inner.DeleteAsync(id, ct);
    }

    public Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default)
    {
        return inner.SetResultAsync(id, result, ct);
    }

    public Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        return inner.AddFileResultAsync(result, ct);
    }

    public Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default)
    {
        return inner.GetCompletedJobWithFileResultsAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            ct);
    }

    public Task SetCancelledAsync(Guid id, CancellationToken ct = default)
    {
        return inner.SetCancelledAsync(id, ct);
    }

    public Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default)
    {
        return inner.UpdateAiConfigAsync(id, connectionId, model, ct);
    }
}
