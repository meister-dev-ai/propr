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
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var activeJobs = dbContext.ReviewJobs
            .Where(job => job.ClientId == clientId)
            .Where(job => job.Status == JobStatus.Pending || job.Status == JobStatus.Processing);

        if (request.CodeReview is not null)
        {
            var review = request.CodeReview;
            var reviewJobs = activeJobs.Where(job =>
                job.Provider == review.Repository.Host.Provider
                && job.HostBaseUrl == review.Repository.Host.HostBaseUrl
                && job.RepositoryId == review.Repository.ExternalRepositoryId
                && job.RepositoryOwnerOrNamespace == review.Repository.OwnerOrNamespace
                && job.RepositoryProjectPath == review.Repository.ProjectPath
                && job.CodeReviewPlatformKind == review.Platform
                && job.ExternalCodeReviewId == review.ExternalReviewId
                && job.PullRequestId == review.Number);

            if (request.ReviewRevision is not null)
            {
                var revision = request.ReviewRevision;
                return reviewJobs.FirstOrDefaultAsync(
                    job => job.RevisionHeadSha == revision.HeadSha
                           && job.RevisionBaseSha == revision.BaseSha
                           && job.RevisionStartSha == revision.StartSha
                           && job.ProviderRevisionId == revision.ProviderRevisionId
                           && job.ReviewPatchIdentity == revision.PatchIdentity,
                    cancellationToken);
            }

            var compatibilityIterationId = ResolveCompatibilityIterationId(request);
            return reviewJobs.FirstOrDefaultAsync(
                job => job.IterationId == compatibilityIterationId,
                cancellationToken);
        }

        return activeJobs.FirstOrDefaultAsync(
            job => job.OrganizationUrl == request.ProviderScopePath
                   && job.ProjectId == request.ProviderProjectKey
                   && job.RepositoryId == request.RepositoryId
                   && job.PullRequestId == request.PullRequestId
                   && job.IterationId == request.IterationId,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReviewJob> CreatePendingJobAsync(
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            ResolveCompatibilityOrganizationUrl(request),
            ResolveCompatibilityProjectId(request),
            ResolveCompatibilityRepositoryId(request),
            ResolveCompatibilityPullRequestId(request),
            ResolveCompatibilityIterationId(request));

        if (request.CodeReview is not null)
        {
            job.SetProviderReviewContext(request.CodeReview);
        }

        if (request.ReviewRevision is not null)
        {
            job.SetReviewRevision(request.ReviewRevision);
        }

        if (request.ReviewTemperature.HasValue)
        {
            job.SetAiConfig(job.AiConnectionId, job.AiModel, request.ReviewTemperature);
        }

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
    public Task<int> CountActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.ReviewJobs.CountAsync(
            job => job.Status == JobStatus.Pending || job.Status == JobStatus.Processing,
            cancellationToken);
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

    private static string ResolveCompatibilityOrganizationUrl(SubmitReviewJobRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProviderScopePath))
        {
            return request.ProviderScopePath.Trim();
        }

        return request.Host?.HostBaseUrl
               ?? request.Repository?.Host.HostBaseUrl
               ?? request.CodeReview?.Repository.Host.HostBaseUrl
               ?? throw new InvalidOperationException("Review intake request must include a provider host reference.");
    }

    private static string ResolveCompatibilityProjectId(SubmitReviewJobRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProviderProjectKey))
        {
            return request.ProviderProjectKey.Trim();
        }

        return request.Repository?.OwnerOrNamespace
               ?? request.CodeReview?.Repository.OwnerOrNamespace
               ?? throw new InvalidOperationException("Review intake request must include a repository owner or namespace.");
    }

    private static string ResolveCompatibilityRepositoryId(SubmitReviewJobRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.RepositoryId))
        {
            return request.RepositoryId.Trim();
        }

        return request.Repository?.ExternalRepositoryId
               ?? request.CodeReview?.Repository.ExternalRepositoryId
               ?? throw new InvalidOperationException("Review intake request must include a repository identifier.");
    }

    private static int ResolveCompatibilityPullRequestId(SubmitReviewJobRequestDto request)
    {
        if (request.PullRequestId > 0)
        {
            return request.PullRequestId;
        }

        return request.CodeReview?.Number
               ?? throw new InvalidOperationException("Review intake request must include a code review number.");
    }

    private static int ResolveCompatibilityIterationId(SubmitReviewJobRequestDto request)
    {
        if (request.IterationId > 0)
        {
            return request.IterationId;
        }

        if (request.ReviewRevision is { ProviderRevisionId: not null } revision
            && int.TryParse(revision.ProviderRevisionId, out var parsedRevisionId)
            && parsedRevisionId > 0)
        {
            return parsedRevisionId;
        }

        return 1;
    }
}
