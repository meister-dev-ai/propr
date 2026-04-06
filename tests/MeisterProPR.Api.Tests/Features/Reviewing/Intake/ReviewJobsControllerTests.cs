// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Controllers;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Api.Features.Reviewing.Intake.Controllers;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
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
        var submitHandler = new SubmitReviewJobHandler(
            store,
            Substitute.For<IReviewExecutionQueue>(),
            NullLogger<SubmitReviewJobHandler>.Instance);
        var queryHandler = new GetReviewJobStatusHandler(store);
        var validator = Substitute.For<IAdoTokenValidator>();
        var controller = new ReviewJobsController(submitHandler, queryHandler, validator, NullLogger<ReviewJobsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.Path = "/clients/test/reviewing/jobs";

        var result = await controller.SubmitReview(Guid.NewGuid(), "valid-ado-token", new SubmitReviewJobRequestDto("https://dev.azure.com/org", "proj", "repo", 42, 1), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task SubmitReview_DuplicateJob_ReturnsConflictResponse()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.FindActiveJobAsync("https://dev.azure.com/org", "proj", "repo", 42, 1, Arg.Any<CancellationToken>())
            .Returns(new MeisterProPR.Domain.Entities.ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", 42, 1));

        var submitHandler = new SubmitReviewJobHandler(
            store,
            Substitute.For<IReviewExecutionQueue>(),
            NullLogger<SubmitReviewJobHandler>.Instance);
        var queryHandler = new GetReviewJobStatusHandler(store);
        var validator = Substitute.For<IAdoTokenValidator>();
        validator.IsValidAsync("valid-ado-token", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        var controller = new ReviewJobsController(submitHandler, queryHandler, validator, NullLogger<ReviewJobsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [clientId] = ClientRole.ClientAdministrator };
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.Path = "/clients/test/reviewing/jobs";

        var result = await controller.SubmitReview(clientId, "valid-ado-token", new SubmitReviewJobRequestDto("https://dev.azure.com/org", "proj", "repo", 42, 1), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.IsType<ReviewJobAcceptedResponse>(conflict.Value);
    }

    [Fact]
    public async Task GetReview_ValidRequest_ReturnsMappedStatusResponse()
    {
        var jobId = Guid.NewGuid();
        var store = Substitute.For<IReviewJobIntakeStore>();
        var job = new MeisterProPR.Domain.Entities.ReviewJob(jobId, Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 1)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Result = new MeisterProPR.Domain.ValueObjects.ReviewResult("Looks good", [new MeisterProPR.Domain.ValueObjects.ReviewComment("file.cs", 10, CommentSeverity.Warning, "Note")]),
        };
        store.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);

        var submitHandler = new SubmitReviewJobHandler(
            store,
            Substitute.For<IReviewExecutionQueue>(),
            NullLogger<SubmitReviewJobHandler>.Instance);
        var queryHandler = new GetReviewJobStatusHandler(store);

        var validator = Substitute.For<IAdoTokenValidator>();
        validator.IsValidAsync("valid-ado-token", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        var controller = new ReviewJobsController(submitHandler, queryHandler, validator, NullLogger<ReviewJobsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.GetReview("valid-ado-token", null, jobId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ReviewStatusResponse>(ok.Value);
        Assert.Equal(jobId, payload.JobId);
        Assert.NotNull(payload.Result);
        Assert.Equal("Looks good", payload.Result!.Summary);
    }
}
