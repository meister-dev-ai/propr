// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceProjectionQueries
{
    internal static IQueryable<CodeInsightPullRequest> Stale(MeisterProPRDbContext db)
    {
        return db.CodeInsightPullRequests.Where(pr =>
            // The projection is absent or uses an older schema.
            pr.PerformanceProjectionVersion != Rollups.ReviewerPerformanceCountProjector.ProjectionVersion
            || pr.PerformanceProjectedAt == null
            // Source collection or an explicit dirty marker requires replacement.
            || pr.MissHarvestObservedAt > pr.PerformanceProjectedAt
            || db.CodeInsightPerformanceDirty.Any(row => row.CodeInsightPullRequestId == pr.Id)
            // Finding metadata and miss judgements can change independently of collection coverage.
            || db.CodeInsightFindings.Any(row =>
                row.CodeInsightPullRequestId == pr.Id
                && (row.PerformanceEvidenceUpdatedAt > pr.PerformanceProjectedAt
                    || row.ClassifiedAt > pr.PerformanceProjectedAt
                    || row.CreatedAt > pr.PerformanceProjectedAt
                    || row.DuplicateVerifiedAt > pr.PerformanceProjectedAt))
            || db.CodeInsightMisses.Any(row =>
                row.CodeInsightPullRequestId == pr.Id
                && row.LastJudgedAt > pr.PerformanceProjectedAt));
    }
}
