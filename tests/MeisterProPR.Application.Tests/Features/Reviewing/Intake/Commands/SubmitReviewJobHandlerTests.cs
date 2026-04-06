// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Intake.Commands;

public sealed class SubmitReviewJobHandlerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private static SubmitReviewJobRequestDto CreateRequest()
    {
        return new SubmitReviewJobRequestDto(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            42,
            3);
    }

    [Fact]
    public async Task HandleAsync_ExistingActiveJob_ReturnsDuplicateWithoutQueueing()
    {
        var request = CreateRequest();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var existingJob = new ReviewJob(Guid.NewGuid(), ClientId, request.OrganizationUrl, request.ProjectId, request.RepositoryId, request.PullRequestId, request.IterationId);
        store.FindActiveJobAsync(request.OrganizationUrl, request.ProjectId, request.RepositoryId, request.PullRequestId, request.IterationId, Arg.Any<CancellationToken>())
            .Returns(existingJob);

        var sut = new SubmitReviewJobHandler(store, queue, NullLogger<SubmitReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new SubmitReviewJobCommand(ClientId, request));

        Assert.True(result.IsDuplicate);
        Assert.Equal(existingJob.Id, result.JobId);
        await store.DidNotReceive().CreatePendingJobAsync(Arg.Any<Guid>(), Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NewJob_CreatesQueuesAndUpdatesPrContext()
    {
        var request = CreateRequest();
        var createdJob = new ReviewJob(Guid.NewGuid(), ClientId, request.OrganizationUrl, request.ProjectId, request.RepositoryId, request.PullRequestId, request.IterationId);
        var store = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();

        store.FindActiveJobAsync(request.OrganizationUrl, request.ProjectId, request.RepositoryId, request.PullRequestId, request.IterationId, Arg.Any<CancellationToken>())
            .Returns((ReviewJob?)null);
        store.CreatePendingJobAsync(ClientId, request, Arg.Any<CancellationToken>())
            .Returns(createdJob);
        pullRequestFetcher.FetchAsync(
                request.OrganizationUrl,
                request.ProjectId,
                request.RepositoryId,
                request.PullRequestId,
                request.IterationId,
                null,
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(new PullRequest(
                request.OrganizationUrl,
                request.ProjectId,
                request.RepositoryId,
                "repo-display",
                request.PullRequestId,
                request.IterationId,
                "Add feature",
                null,
                "refs/heads/feature/x",
                "refs/heads/main",
                []));

        var sut = new SubmitReviewJobHandler(store, queue, NullLogger<SubmitReviewJobHandler>.Instance, pullRequestFetcher);

        var result = await sut.HandleAsync(new SubmitReviewJobCommand(ClientId, request));

        Assert.False(result.IsDuplicate);
        Assert.Equal(createdJob.Id, result.JobId);
        await store.Received(1).CreatePendingJobAsync(ClientId, request, Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(createdJob.Id, Arg.Any<CancellationToken>());
        await store.Received(1).UpdatePrContextAsync(createdJob.Id, "Add feature", "repo-display", "refs/heads/feature/x", "refs/heads/main", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PullRequestContextFailure_StillReturnsAcceptedResult()
    {
        var request = CreateRequest();
        var createdJob = new ReviewJob(Guid.NewGuid(), ClientId, request.OrganizationUrl, request.ProjectId, request.RepositoryId, request.PullRequestId, request.IterationId);
        var store = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();

        store.FindActiveJobAsync(request.OrganizationUrl, request.ProjectId, request.RepositoryId, request.PullRequestId, request.IterationId, Arg.Any<CancellationToken>())
            .Returns((ReviewJob?)null);
        store.CreatePendingJobAsync(ClientId, request, Arg.Any<CancellationToken>())
            .Returns(createdJob);
        pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<PullRequest>>(_ => throw new InvalidOperationException("ADO unavailable"));

        var sut = new SubmitReviewJobHandler(store, queue, NullLogger<SubmitReviewJobHandler>.Instance, pullRequestFetcher);

        var result = await sut.HandleAsync(new SubmitReviewJobCommand(ClientId, request));

        Assert.False(result.IsDuplicate);
        Assert.Equal(createdJob.Id, result.JobId);
        await queue.Received(1).EnqueueAsync(createdJob.Id, Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpdatePrContextAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
