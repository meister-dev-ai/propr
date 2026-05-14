// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.Entities;

public sealed class ReviewModeRunResultTests
{
    [Fact]
    public void MarkCompleted_WithCompletionBeforeStartedAt_Throws()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sut = CreateResult();
        sut.MarkProcessing(startedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            sut.MarkCompleted(
                new ReviewResult("summary", []),
                startedAt.AddMinutes(-1)));
    }

    [Fact]
    public void MarkFailed_WithCompletionBeforeStartedAt_Throws()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sut = CreateResult();
        sut.MarkProcessing(startedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => sut.MarkFailed(startedAt.AddMinutes(-1)));
    }

    [Fact]
    public void MarkCompleted_WithCompletionAfterStartedAt_Succeeds()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var completedAt = startedAt.AddMinutes(1);
        var sut = CreateResult();
        sut.MarkProcessing(startedAt);

        sut.MarkCompleted(new ReviewResult("summary", []), completedAt);

        Assert.Equal(JobStatus.Completed, sut.Status);
        Assert.Equal(completedAt, sut.CompletedAt);
    }

    private static ReviewModeRunResult CreateResult()
    {
        return new ReviewModeRunResult(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReviewStrategy.PrWideAgentic,
            ReviewPublicationMode.Publish);
    }
}
