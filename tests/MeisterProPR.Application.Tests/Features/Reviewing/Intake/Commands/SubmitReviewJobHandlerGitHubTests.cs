// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Intake.Commands;

public sealed class SubmitReviewJobHandlerGitHubTests
{
    [Fact]
    public async Task HandleAsync_GitHubRequest_CreatesAndQueuesProviderNeutralJob()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var request = new SubmitReviewJobRequestDto(
            host.HostBaseUrl,
            repository.OwnerOrNamespace,
            repository.ExternalRepositoryId,
            42,
            1)
        {
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42),
            ReviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"),
        };

        var createdJob = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);
        createdJob.SetProviderReviewContext(request.CodeReview!);
        createdJob.SetReviewRevision(request.ReviewRevision);

        var intakeStore = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        intakeStore.FindActiveJobAsync(clientId, request, Arg.Any<CancellationToken>()).Returns((ReviewJob?)null);
        intakeStore.CreatePendingJobAsync(clientId, request, Arg.Any<CancellationToken>()).Returns(createdJob);

        var sut = new SubmitReviewJobHandler(intakeStore, queue, NullLogger<SubmitReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new SubmitReviewJobCommand(clientId, request));

        Assert.False(result.IsDuplicate);
        Assert.Equal(createdJob.Id, result.JobId);
        await queue.Received(1).EnqueueAsync(createdJob.Id, Arg.Any<CancellationToken>());
    }
}
