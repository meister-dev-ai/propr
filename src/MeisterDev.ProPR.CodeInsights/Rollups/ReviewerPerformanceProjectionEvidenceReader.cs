// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

internal static class ReviewerPerformanceProjectionEvidenceReader
{
    internal static async Task<ReviewerPerformanceProjectionEvidence> LoadAsync(MeisterProPRDbContext db, Guid aggregateId, CancellationToken ct)
    {
        var findings = await db.CodeInsightFindings.AsNoTracking().Where(row => row.CodeInsightPullRequestId == aggregateId)
            .Select(row => new ReviewerPerformanceFindingEvidence(
                row.Id,
                row.ObservedAt,
                row.OriginModelId,
                row.OriginLogicalModelName,
                row.Qualifier,
                row.ClassifiedAt,
                row.ProviderScope,
                row.ProviderCommentId,
                row.ProviderThreadId,
                row.PublicationState,
                row.DuplicateState,
                row.NativeStatus,
                row.CurrentDisposition,
                row.CurrentClassifierVersion,
                row.CurrentClassifierConfidence)).ToListAsync(ct);
        var ids = findings.Select(row => row.Id).ToList();
        var dispositions = await db.CodeInsightFindingDispositions.AsNoTracking().Where(row => ids.Contains(row.CodeInsightFindingId))
            .Select(row => new ReviewerPerformanceDispositionEvidence(
                row.CodeInsightFindingId,
                row.Disposition,
                row.NativeStatus,
                row.ClassifierVersion,
                row.ClassifierConfidence))
            .ToDictionaryAsync(row => row.CodeInsightFindingId, ct);
        var tags = await db.CodeInsightFindingTags.AsNoTracking().Where(row => ids.Contains(row.CodeInsightFindingId) && row.IsCore && row.CoreSlug != null)
            .Select(row => new ReviewerPerformanceTagEvidence(
                row.CodeInsightFindingId,
                row.CoreSlug)).ToListAsync(ct);
        var memberships = tags.GroupBy(row => row.CodeInsightFindingId).ToDictionary(
            group => group.Key, group => string.Join('|', group.Select(row => row.CoreSlug!).Distinct().Order(StringComparer.Ordinal)));
        var misses = await db.CodeInsightMisses.AsNoTracking().Where(row => row.CodeInsightPullRequestId == aggregateId)
            .Select(row => new ReviewerPerformanceMissEvidence(
                row.HarvestedAt,
                row.IsSubstantive,
                row.IsInScope,
                row.WasActedOn,
                row.JudgedThreadResolved,
                row.TypeMembership,
                row.Qualifier,
                row.JudgementFailed,
                row.DimensionClassifierVersion,
                row.DimensionJudgementFailed,
                row.ProviderScope,
                row.ExcludedAsOwnFinding)).ToListAsync(ct);
        var exposures = await db.CodeInsightReviewExposures.AsNoTracking().Where(row => row.CodeInsightPullRequestId == aggregateId)
            .Select(row => new ReviewerPerformanceExposureEvidence(
                row.ObservedAt,
                row.ProviderScope)).ToListAsync(ct);
        var coverage = await db.CodeInsightHarvestCoverage.AsNoTracking().Where(row => row.CodeInsightPullRequestId == aggregateId).ToListAsync(ct);
        return new(findings, dispositions, memberships, misses, exposures, coverage);
    }
}
