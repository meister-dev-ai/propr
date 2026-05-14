// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Features.Reviewing.TestDoubles;

internal static class StrategyReviewJobTestData
{
    public static ReviewJob PendingJob(
        ReviewStrategy strategy = ReviewStrategy.FileByFile,
        ReviewComparisonMode comparisonMode = ReviewComparisonMode.Single,
        ReviewPublicationMode publicationMode = ReviewPublicationMode.Publish)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            "repository",
            123,
            1);

        job.SelectReviewStrategy(strategy, ReviewStrategySelectionSource.JobOverride, comparisonMode, publicationMode, null);
        return job;
    }
}
