// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Reviewing.Contracts;
using MeisterDev.ProPR.Api.Features.Reviewing.Intake.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Reviewing.Intake;

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

        var result = await controller.RestartReview(Guid.NewGuid(), Substitute.For<IThreadPassJobRepository>(), CancellationToken.None);

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

        var result = await controller.RestartReview(jobId, Substitute.For<IThreadPassJobRepository>(), CancellationToken.None);

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

        var result = await controller.RestartReview(jobId, Substitute.For<IThreadPassJobRepository>(), CancellationToken.None);

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

        var result = await controller.RestartReview(jobId, Substitute.For<IThreadPassJobRepository>(), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ReviewJobRestartResponse>(accepted.Value);
        Assert.Equal(jobId, payload.SourceJobId);
        Assert.NotEqual(Guid.Empty, payload.JobId);
        await queue.Received(1).EnqueueAsync(payload.JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestartReview_BudgetHeldThreadPass_QueuesItAgainUnderItsOwnIdentity()
    {
        var passId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(passId, Arg.Any<CancellationToken>()).Returns((ReviewJob?)null);

        var threadPasses = Substitute.For<IThreadPassJobRepository>();
        threadPasses.GetByIdAsync(passId, Arg.Any<CancellationToken>())
            .Returns(
                new ThreadPassJob(
                    passId,
                    clientId,
                    "https://dev.azure.com/org",
                    "proj",
                    "repo",
                    42,
                    1,
                    "1",
                    "1|abc"));
        threadPasses.TryRestartAsync(passId, Arg.Any<CancellationToken>()).Returns(true);

        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.RestartReview(passId, threadPasses, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ReviewJobRestartResponse>(accepted.Value);
        Assert.Equal(passId, payload.SourceJobId);
        Assert.Equal(passId, payload.JobId);
        await threadPasses.Received(1).TryRestartAsync(passId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestartReview_CompletedThreadPass_ReturnsConflict()
    {
        var passId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(passId, Arg.Any<CancellationToken>()).Returns((ReviewJob?)null);

        var threadPasses = Substitute.For<IThreadPassJobRepository>();
        threadPasses.GetByIdAsync(passId, Arg.Any<CancellationToken>())
            .Returns(
                new ThreadPassJob(
                    passId,
                    clientId,
                    "https://dev.azure.com/org",
                    "proj",
                    "repo",
                    42,
                    1,
                    "1",
                    "1|abc"));
        threadPasses.TryRestartAsync(passId, Arg.Any<CancellationToken>()).Returns(false);

        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.RestartReview(passId, threadPasses, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task StopReview_WithoutAdminRole_ReturnsForbidden()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));
        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.StopReview(jobId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task StopReview_UnknownJob_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns((ReviewJob?)null);
        var controller = CreateController(store, Guid.NewGuid(), ClientRole.ClientAdministrator);

        var result = await controller.StopReview(jobId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task StopReview_RunningJobAsAdmin_ReturnsOkStoppedAndPersistsStop()
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
                    Status = JobStatus.Processing,
                });
        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, jobRepository: jobRepository);

        var result = await controller.StopReview(jobId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ReviewJobStopResponse>(ok.Value);
        Assert.Equal(jobId, response.JobId);
        Assert.Equal("stopped", response.Status);
        await jobRepository.Received(1).SetStoppedAsync(jobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopReview_TerminalJob_ReturnsConflictWithoutStopping()
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
        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, jobRepository: jobRepository);

        var result = await controller.StopReview(jobId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        await jobRepository.DidNotReceive().SetStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WithoutAnyClientRole_ReturnsForbiddenWithoutTriggeringAReview()
    {
        var synchronization = SubstituteSynchronization(SubmittedOutcome(Guid.NewGuid()));
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            null,
            null,
            synchronization: synchronization);
        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();

        var result = await controller.SubmitReviewByCoordinates(
            Guid.NewGuid(),
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(objectResult.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, payload.Outcome);
        await synchronization.DidNotReceive()
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_AsClientUser_ReturnsAcceptedWithTheJobId()
    {
        // Triggering a review spends money but is deliberately not administrator-gated, matching restart.
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            synchronization: SubstituteSynchronization(SubmittedOutcome(jobId)));

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(accepted.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, payload.Outcome);
        Assert.Equal(jobId, payload.JobId);
    }

    [Theory]
    [InlineData(null, "acme", "12345", 7)]
    [InlineData("   ", "acme", "12345", 7)]
    [InlineData("https://github.example.com", null, "12345", 7)]
    [InlineData("https://github.example.com", "   ", "12345", 7)]
    [InlineData("https://github.example.com", "acme", null, 7)]
    [InlineData("https://github.example.com", "acme", "   ", 7)]
    [InlineData("https://github.example.com", "acme", "12345", null)]
    [InlineData("https://github.example.com", "acme", "12345", 0)]
    [InlineData("https://github.example.com", "acme", "12345", -1)]
    public async Task SubmitReviewByCoordinates_WithIncompleteCoordinates_ReturnsBadRequestWithoutTriggeringAReview(
        string? providerScopePath,
        string? providerProjectKey,
        string? repositoryId,
        int? pullRequestId)
    {
        var clientId = Guid.NewGuid();
        var synchronization = SubstituteSynchronization(SubmittedOutcome(Guid.NewGuid()));
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            synchronization: synchronization);

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            new SubmitReviewByCoordinatesRequest(providerScopePath, providerProjectKey, repositoryId, pullRequestId),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await synchronization.DidNotReceive()
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WhenQueueingTheReviewFails_ReturnsInternalServerError()
    {
        var clientId = Guid.NewGuid();
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns<PullRequestSynchronizationOutcome>(_ => throw new InvalidOperationException("job store is down"));
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            synchronization: synchronization);

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(objectResult.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.SubmissionFailed, payload.Outcome);
        Assert.Null(payload.JobId);
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WhenNoConfigurationCoversTheCoordinates_ReturnsForbidden()
    {
        var clientId = Guid.NewGuid();
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            crawlConfigurations: SubstituteCrawlRepository(null));

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(objectResult.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, payload.Outcome);
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WhenAJobIsAlreadyRunning_ReturnsConflictWithThatJobId()
    {
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            synchronization: SubstituteSynchronization(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.DuplicateActiveJob,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Skipped duplicate active job for PR #7."],
                    jobId)));

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(conflict.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.DuplicateActiveJob, payload.Outcome);
        Assert.Equal(jobId, payload.JobId);
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WhenTheProviderHasNoSuchPullRequest_ReturnsNotFound()
    {
        var clientId = Guid.NewGuid();
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            providerRegistry: SubstituteRegistry(SubstituteQueryService(null)));

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(notFound.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.PullRequestNotFound, payload.Outcome);
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WhenTheProviderCannotBeReached_ReturnsBadGateway()
    {
        var clientId = Guid.NewGuid();
        var queryService = Substitute.For<ICodeReviewQueryService>();
        queryService.Provider.Returns(ScmProvider.GitHub);
        queryService.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns<ReviewDiscoveryItemDto?>(_ => throw new InvalidOperationException("host unreachable"));
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            providerRegistry: SubstituteRegistry(queryService));

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(objectResult.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.RevisionUnresolvable, payload.Outcome);
    }

    [Fact]
    public async Task SubmitReviewByCoordinates_WhenThePullRequestIsClosed_ReturnsConflictWithTheReason()
    {
        var clientId = Guid.NewGuid();
        var controller = CreateController(
            Substitute.For<IReviewJobIntakeStore>(),
            clientId,
            ClientRole.ClientUser,
            providerRegistry: SubstituteRegistry(SubstituteQueryService(OpenPullRequest() with { ReviewState = CodeReviewState.Merged })));

        var result = await controller.SubmitReviewByCoordinates(
            clientId,
            CreateCoordinatesRequest(),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var payload = Assert.IsType<ReviewByCoordinatesResponse>(conflict.Value);
        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotSubmittable, payload.Outcome);
        Assert.NotNull(payload.Reason);
    }

    private static ReviewJobsController CreateController(
        IReviewJobIntakeStore store,
        Guid? clientId,
        ClientRole? role,
        IReviewExecutionQueue? queue = null,
        IJobRepository? jobRepository = null,
        ICrawlConfigurationRepository? crawlConfigurations = null,
        IScmProviderRegistry? providerRegistry = null,
        IPullRequestSynchronizationService? synchronization = null)
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
        var stopHandler = new StopReviewJobHandler(
            jobRepository ?? Substitute.For<IJobRepository>(),
            Substitute.For<IReviewJobCancellationRegistry>(),
            NullLogger<StopReviewJobHandler>.Instance);
        var byCoordinatesHandler = new SubmitReviewByCoordinatesHandler(
            crawlConfigurations ?? SubstituteCrawlRepository(clientId),
            SubstituteWebhookRepository(),
            providerRegistry ?? SubstituteRegistry(SubstituteQueryService(OpenPullRequest())),
            synchronization ?? SubstituteSynchronization(SubmittedOutcome(Guid.NewGuid())),
            NullLogger<SubmitReviewByCoordinatesHandler>.Instance);
        var controller = new ReviewJobsController(
            submitHandler,
            restartHandler,
            stopHandler,
            queryHandler,
            byCoordinatesHandler,
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

    private static SubmitReviewByCoordinatesRequest CreateCoordinatesRequest()
    {
        return new SubmitReviewByCoordinatesRequest("https://github.example.com", "acme", "12345", 7);
    }

    private static ProviderHostRef GitHubHost()
    {
        return new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
    }

    private static ReviewDiscoveryItemDto OpenPullRequest()
    {
        var repository = new RepositoryRef(GitHubHost(), "12345", "acme", "acme/propr", "propr");

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitHub,
            repository,
            new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7),
            CodeReviewState.Open,
            new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha"),
            null,
            "Add coordinate-addressed review intake",
            "https://github.example.com/acme/propr/pull/7",
            "feature/intake",
            "main");
    }

    private static PullRequestSynchronizationOutcome SubmittedOutcome(Guid jobId)
    {
        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.Submitted,
            PullRequestSynchronizationLifecycleDecision.None,
            ["Submitted review intake job for PR #7."],
            jobId);
    }

    private static IPullRequestSynchronizationService SubstituteSynchronization(PullRequestSynchronizationOutcome outcome)
    {
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
        return synchronization;
    }

    private static ICodeReviewQueryService SubstituteQueryService(ReviewDiscoveryItemDto? review)
    {
        var queryService = Substitute.For<ICodeReviewQueryService>();
        queryService.Provider.Returns(ScmProvider.GitHub);
        queryService.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns(review);
        return queryService;
    }

    private static IScmProviderRegistry SubstituteRegistry(ICodeReviewQueryService queryService)
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        registry.GetCodeReviewQueryService(Arg.Any<ScmProvider>()).Returns(queryService);
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>())
            .Returns(Substitute.For<IRepositoryDiscoveryProvider>());
        return registry;
    }

    /// <summary>A crawl configuration covering the fixture coordinates, unless the caller has no client.</summary>
    private static ICrawlConfigurationRepository SubstituteCrawlRepository(Guid? clientId)
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(
                clientId.HasValue
                    ?
                    [
                        new CrawlConfigurationDto(
                            Guid.NewGuid(),
                            clientId.Value,
                            ScmProvider.GitHub,
                            "https://github.example.com",
                            "acme",
                            300,
                            true,
                            DateTimeOffset.UnixEpoch,
                            [
                                new CrawlRepoFilterDto(
                                    Guid.NewGuid(),
                                    "propr",
                                    [],
                                    new CanonicalSourceReferenceDto("gitHub", "12345"),
                                    "propr"),
                            ]),
                    ]
                    : []);
        return repository;
    }

    private static IWebhookConfigurationRepository SubstituteWebhookRepository()
    {
        var repository = Substitute.For<IWebhookConfigurationRepository>();
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        return repository;
    }
}
