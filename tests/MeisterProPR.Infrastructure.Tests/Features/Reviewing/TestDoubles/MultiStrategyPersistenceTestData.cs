// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.TestDoubles;

internal static class MultiStrategyPersistenceTestData
{
    public static ReviewJob PendingPrWideJob(Guid? comparisonGroupId = null)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            "repository",
            456,
            2);

        job.SelectReviewStrategy(
            ReviewStrategy.PrWideAgentic,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            comparisonGroupId);

        return job;
    }
}
