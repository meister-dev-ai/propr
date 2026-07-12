// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Api.Features.Reviewing.Intake.Controllers;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Reviewing.Intake;

public sealed class ReviewJobsControllerTests
{
    [Fact]
    public async Task SubmitReview_WithoutRequiredRole_ReturnsForbidden()
    {
        var store = Substitute.For<IReviewJobIntakeStore>();
        var controller = CreateController(store, Guid.NewGuid(), null);

        var result = await controller.SubmitReview(Guid.NewGuid(), CreateAzureDevOpsRequest(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task SubmitReview_DuplicateJob_ReturnsConflictResponse()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.FindActiveJobAsync(clientId, Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));

        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator);

        var result = await controller.SubmitReview(clientId, CreateAzureDevOpsRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.IsType<ReviewJobAcceptedResponse>(conflict.Value);
    }

    [Fact]
    public async Task GetReview_WithoutAuthenticatedCaller_ReturnsUnauthorized()
    {
        var controller = CreateController(Substitute.For<IReviewJobIntakeStore>(), null, null);

        var result = await controller.GetReview(Guid.NewGuid(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetReview_WithoutClientRole_ReturnsForbidden()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));
        var controller = CreateController(store, null, null);
        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();

        var result = await controller.GetReview(jobId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetReview_ValidRequest_ReturnsMappedStatusResponse()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var job = new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        job.ApplyResult(
            new ReviewResult(
                "Looks good",
                [new ReviewComment("file.cs", 10, CommentSeverity.Warning, "Note")]));
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);

        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.GetReview(jobId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ReviewStatusResponse>(ok.Value);
        Assert.Equal(jobId, payload.JobId);
        Assert.NotNull(payload.Result);
        Assert.Equal("Looks good", payload.Result!.Summary);
    }

    [Fact]
    public async Task SubmitReview_AzureDevOpsRequestWithoutAdoToken_ReturnsAccepted()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var request = CreateAzureDevOpsRequest();

        store.FindActiveJobAsync(clientId, Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>())
            .Returns((ReviewJob?)null);
        store.CreatePendingJobAsync(clientId, Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));

        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, queue);

        var result = await controller.SubmitReview(clientId, request, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ReviewJobAcceptedResponse>(accepted.Value);
        Assert.Equal(ScmProvider.AzureDevOps, payload.Provider);
        Assert.Equal("42", payload.CodeReview!.ExternalReviewId);
    }

    [Fact]
    public async Task GetReview_WithPrLevelPublishableFinding_PreservesNullAnchorInStatusResponse()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var job = new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        job.ApplyResult(
            new ReviewResult(
                "PR-wide review identified one publishable cross-file finding.",
                [new ReviewComment(null, null, CommentSeverity.Warning, "Cross-file registration ordering can still publish stale results.")]));
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);

        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.GetReview(jobId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ReviewStatusResponse>(ok.Value);
        var comment = Assert.Single(payload.Result!.Comments);
        Assert.Null(comment.FilePath);
        Assert.Null(comment.LineNumber);
        Assert.Equal(CommentSeverity.Warning, comment.Severity);
        Assert.Equal("Cross-file registration ordering can still publish stale results.", comment.Message);
    }

    [Fact]
    public async Task SubmitReview_LegacyAzureDevOpsRequestShape_ReturnsBadRequest()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator);

        var result = await controller.SubmitReview(
            clientId,
            new SubmitReviewRequest(ScmProvider.AzureDevOps, "https://dev.azure.com/org", null, null, null),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task SubmitReview_GitHubRequestWithoutAdoToken_ReturnsAccepted()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var request = new SubmitReviewRequest(
            ScmProvider.GitHub,
            host.HostBaseUrl,
            new ReviewRepositoryRefDto(
                repository.ExternalRepositoryId,
                repository.OwnerOrNamespace,
                repository.ProjectPath),
            new ReviewCodeReviewRefDto(CodeReviewPlatformKind.PullRequest, "42", 42),
            new ReviewRevisionRefDto("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"));

        store.FindActiveJobAsync(clientId, Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>())
            .Returns((ReviewJob?)null);
        store.CreatePendingJobAsync(clientId, Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewJob(
                    Guid.NewGuid(),
                    clientId,
                    host.HostBaseUrl,
                    repository.OwnerOrNamespace,
                    repository.ExternalRepositoryId,
                    42,
                    1));

        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, queue);

        var result = await controller.SubmitReview(clientId, request, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ReviewJobAcceptedResponse>(accepted.Value);
        Assert.Equal(ScmProvider.GitHub, payload.Provider);
        Assert.Equal("42", payload.CodeReview!.ExternalReviewId);
    }

    [Fact]
    public async Task RestartReview_WithoutAuthenticatedCaller_ReturnsUnauthorized()
    {
        var controller = CreateController(Substitute.For<IReviewJobIntakeStore>(), null, null);

        var result = await controller.RestartReview(Guid.NewGuid(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }

    [Fact]
    public async Task RestartReview_WithoutClientRole_ReturnsForbidden()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));
        var controller = CreateController(store, null, null);
        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();

        var result = await controller.RestartReview(jobId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task RestartReview_NotFailedJob_ReturnsConflict()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetById(jobId)
            .Returns(
                new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1)
                {
                    Status = JobStatus.Completed,
                });

        // ClientUser is sufficient — administrator rights are not required.
        var controller = CreateController(store, clientId, ClientRole.ClientUser, jobRepository: jobRepository);

        var result = await controller.RestartReview(jobId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task RestartReview_FailedJob_AsClientUser_ReturnsAccepted()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetById(jobId)
            .Returns(
                new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1)
                {
                    Status = JobStatus.Failed,
                });
        jobRepository.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var queue = Substitute.For<IReviewExecutionQueue>();
        var controller = CreateController(store, clientId, ClientRole.ClientUser, queue, jobRepository);

        var result = await controller.RestartReview(jobId, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ReviewJobRestartResponse>(accepted.Value);
        Assert.Equal(jobId, payload.SourceJobId);
        Assert.NotEqual(Guid.Empty, payload.JobId);
        await queue.Received(1).EnqueueAsync(payload.JobId, Arg.Any<CancellationToken>());
    }

    private static ReviewJobsController CreateController(
        IReviewJobIntakeStore store,
        Guid? clientId,
        ClientRole? role,
        IReviewExecutionQueue? queue = null,
        IJobRepository? jobRepository = null)
    {
        var submitHandler = new SubmitReviewJobHandler(
            store,
            queue ?? Substitute.For<IReviewExecutionQueue>(),
            NullLogger<SubmitReviewJobHandler>.Instance);
        var queryHandler = new GetReviewJobStatusHandler(store);
        var restartHandler = new RestartReviewJobHandler(
            jobRepository ?? Substitute.For<IJobRepository>(),
            queue ?? Substitute.For<IReviewExecutionQueue>(),
            NullLogger<RestartReviewJobHandler>.Instance);
        var controller = new ReviewJobsController(
            submitHandler,
            restartHandler,
            queryHandler,
            NullLogger<ReviewJobsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.Path = "/clients/test/reviewing/jobs";

        if (clientId.HasValue)
        {
            controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        }

        if (clientId.HasValue && role.HasValue)
        {
            controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
                { [clientId.Value] = role.Value };
        }

        return controller;
    }

    private static SubmitReviewRequest CreateAzureDevOpsRequest()
    {
        return new SubmitReviewRequest(
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/org",
            new ReviewRepositoryRefDto("repo", "proj", "proj"),
            new ReviewCodeReviewRefDto(CodeReviewPlatformKind.PullRequest, "42", 42),
            new ReviewRevisionRefDto("head-sha", "base-sha", "base-sha", "1", "base-sha...head-sha"));
    }
}
