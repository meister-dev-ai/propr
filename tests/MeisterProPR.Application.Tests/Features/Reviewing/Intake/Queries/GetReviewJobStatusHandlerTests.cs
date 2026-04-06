// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Intake.Queries;

public sealed class GetReviewJobStatusHandlerTests
{
    [Fact]
    public async Task HandleAsync_UnknownJob_ReturnsNull()
    {
        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ReviewJob?)null);
        var sut = new GetReviewJobStatusHandler(store);

        var result = await sut.HandleAsync(new GetReviewJobStatusQuery(Guid.NewGuid()));

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_CompletedJob_MapsResultPayload()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 99, 2)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Result = new ReviewResult("Looks good", [new ReviewComment("file.cs", 12, CommentSeverity.Warning, "Double check null handling")]),
        };

        var store = Substitute.For<IReviewJobIntakeStore>();
        store.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var sut = new GetReviewJobStatusHandler(store);

        var result = await sut.HandleAsync(new GetReviewJobStatusQuery(job.Id));

        Assert.NotNull(result);
        Assert.Equal(job.Id, result!.JobId);
        Assert.Equal(JobStatus.Completed, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal("Looks good", result.Result!.Summary);
        Assert.Single(result.Result.Comments);
        Assert.Equal("file.cs", result.Result.Comments[0].FilePath);
    }
}
