// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using MeisterDev.ProPR.Application.Interfaces;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Offline;

/// <summary>
///     The submission window on the in-memory store, which the offline path uses. It feeds the bound on how
///     many reviews one pull request has an AI perform within the hour, so it has to count the same jobs the
///     relational store counts.
/// </summary>
public sealed class InMemoryReviewJobRepositorySubmissionWindowTests
{
    [Fact]
    public async Task DuplicateMatchingUsesTheSuppliedSourceIdentityPolicy()
    {
        var policy = Substitute.For<IReviewSourcePolicy>();
        policy.GetRepositoryIdentityKey(default!, default!, default!, default, default).ReturnsForAnyArgs("same-repository");
        policy.Provider.Returns(ScmProvider.AzureDevOps);
        var repository = new InMemoryReviewJobRepository([policy]);
        var clientId = Guid.NewGuid();
        var first = new ReviewJob(Guid.NewGuid(), clientId, OrganizationUrl, "project", "native", 42, 1);
        var alias = new ReviewJob(Guid.NewGuid(), clientId, OrganizationUrl, "project", "alias", 42, 1);
        await repository.AddAsync(first);
        var result = await repository.TryAddIfNoActiveDuplicateAsync(alias);
        Assert.False(result.WasAdded);
        Assert.Same(first, result.DuplicateJob);
    }

    private const string OrganizationUrl = "https://dev.azure.com/org";

    private readonly InMemoryReviewJobRepository _jobs = new();

    [Fact]
    public async Task GetSubmissionWindowAsync_LeavesOutJobsThatMadeNoModelCall()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId);
        await this._jobs.AddAsync(asking);

        // One review that ran to the end and one still running: the first spent tokens, the second is going to,
        // so both count.
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.Completed, inputTokens: 1_200, outputTokens: 400));
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.Processing));

        // A job still queued, a refused job and a job waiting out its hold had no model call made for them.
        await this._jobs.AddAsync(MakeJob(clientId));
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.AdmissionRefused));
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.AdmissionHeld));

        // A review that ended before its first model call, for example because it was cancelled while it was
        // fetching the repository.
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.Failed));

        var window = await this._jobs.GetSubmissionWindowAsync(
            clientId,
            OrganizationUrl,
            "proj",
            "repo",
            42,
            DateTimeOffset.UtcNow.AddHours(-1),
            asking.Id);

        Assert.Equal(2, window.Count);
    }

    // A push that arrives while the previous review is running supersedes it. The superseded review has
    // already spent what the bound measures, so it counts whatever status it was superseded from.
    [Fact]
    public async Task GetSubmissionWindowAsync_CountsASupersededJobThatSpentTokens()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId);
        await this._jobs.AddAsync(asking);
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.Superseded, inputTokens: 900, outputTokens: 300));

        var window = await this._jobs.GetSubmissionWindowAsync(
            clientId,
            OrganizationUrl,
            "proj",
            "repo",
            42,
            DateTimeOffset.UtcNow.AddHours(-1),
            asking.Id);

        Assert.Equal(1, window.Count);
    }

    [Fact]
    public async Task GetSubmissionWindowAsync_LeavesOutASupersededJobThatSpentNothing()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId);
        await this._jobs.AddAsync(asking);
        await this._jobs.AddAsync(MakeJob(clientId, JobStatus.Superseded));

        var window = await this._jobs.GetSubmissionWindowAsync(
            clientId,
            OrganizationUrl,
            "proj",
            "repo",
            42,
            DateTimeOffset.UtcNow.AddHours(-1),
            asking.Id);

        Assert.Equal(0, window.Count);
        Assert.Null(window.OldestSubmittedAt);
    }

    private static ReviewJob MakeJob(
        Guid clientId,
        JobStatus status = JobStatus.Pending,
        long inputTokens = 0,
        long outputTokens = 0)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId, OrganizationUrl, "proj", "repo", 42, 1)
        {
            Status = status,
        };
        if (inputTokens > 0 || outputTokens > 0)
        {
            job.AccumulateTokens(inputTokens, outputTokens);
        }

        return job;
    }
}
