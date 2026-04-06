// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Intake.Persistence;

/// <summary>EF Core implementation of the review-job intake store.</summary>
public sealed class EfReviewJobIntakeStore(MeisterProPRDbContext dbContext) : IReviewJobIntakeStore
{
    /// <inheritdoc />
    public Task<ReviewJob?> FindActiveJobAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.ReviewJobs.FirstOrDefaultAsync(
            job => job.OrganizationUrl == organizationUrl
                && job.ProjectId == projectId
                && job.RepositoryId == repositoryId
                && job.PullRequestId == pullRequestId
                && job.IterationId == iterationId
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.Processing),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReviewJob> CreatePendingJobAsync(
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            request.OrganizationUrl,
            request.ProjectId,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);

        dbContext.ReviewJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        return job;
    }

    /// <inheritdoc />
    public Task<ReviewJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return dbContext.ReviewJobs.FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdatePrContextAsync(
        Guid jobId,
        string? title,
        string? repositoryName,
        string? sourceBranch,
        string? targetBranch,
        CancellationToken cancellationToken = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([jobId], cancellationToken);
        if (job is null)
        {
            return;
        }

        job.SetPrContext(title, repositoryName, sourceBranch, targetBranch);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
