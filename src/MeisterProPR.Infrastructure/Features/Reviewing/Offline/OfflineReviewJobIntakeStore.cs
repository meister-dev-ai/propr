// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory review-job intake store for offline Reviewing execution.
/// </summary>
public sealed class OfflineReviewJobIntakeStore(InMemoryReviewJobRepository jobs) : IReviewJobIntakeStore
{
    public Task<ReviewJob?> FindActiveJobAsync(
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(jobs.FindActiveJob(
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId));
    }

    public async Task<ReviewJob> CreatePendingJobAsync(
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);

        if (request.ReviewTemperature.HasValue)
        {
            job.SetAiConfig(job.AiConnectionId, job.AiModel, request.ReviewTemperature);
        }

        await jobs.AddAsync(job, cancellationToken);
        return job;
    }

    public Task<ReviewJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(jobs.GetById(jobId));
    }

    public Task<int> CountActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return jobs.CountProcessingJobsAsync(cancellationToken)
            .ContinueWith(task => jobs.GetPendingJobs().Count + task.Result, cancellationToken);
    }

    public Task UpdatePrContextAsync(
        Guid jobId,
        string? title,
        string? repositoryName,
        string? sourceBranch,
        string? targetBranch,
        CancellationToken cancellationToken = default)
    {
        return jobs.UpdatePrContextAsync(jobId, title, repositoryName, sourceBranch, targetBranch, cancellationToken);
    }
}
