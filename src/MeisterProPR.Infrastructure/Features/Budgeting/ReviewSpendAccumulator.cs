// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Computes accumulated review spend per budget scope from the persisted per-job USD cost
///     (<see cref="ReviewJob.TotalEstimatedCostUsd" />) and the per-client daily usage samples. All sums are
///     null-aware: an unpriced contribution is omitted from the total and flags the scope approximate rather than
///     being coerced to zero.
/// </summary>
public sealed class ReviewSpendAccumulator(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IClientTokenUsageRepository usageRepository) : IReviewSpendAccumulator
{
    /// <inheritdoc />
    public async Task<ReviewSpendBaseline> GetBaselineAsync(ReviewJob job, DateOnly asOfDate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var clientMonthToDate = await this.SumClientMonthToDateAsync(job.ClientId, asOfDate, ct).ConfigureAwait(false);

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pullRequest = await SumOtherJobCostAsync(context, job, includeIncrementFilter: false, ct).ConfigureAwait(false);
        var increment = await SumOtherJobCostAsync(context, job, includeIncrementFilter: true, ct).ConfigureAwait(false);

        return new ReviewSpendBaseline(clientMonthToDate, pullRequest, increment);
    }

    private async Task<ReviewScopeSpend> SumClientMonthToDateAsync(Guid clientId, DateOnly asOfDate, CancellationToken ct)
    {
        // The monthly client budget resets at the period boundary: only samples dated within the current
        // calendar month count, so a new month automatically starts the total at zero.
        var monthStart = new DateOnly(asOfDate.Year, asOfDate.Month, 1);
        var samples = await usageRepository
            .GetByClientAndDateRangeAsync(clientId, monthStart, asOfDate, ct)
            .ConfigureAwait(false);

        var known = samples
            .Where(sample => sample.EstimatedCostUsd.HasValue)
            .Sum(sample => sample.EstimatedCostUsd!.Value);
        var isApproximate = samples.Any(sample => !sample.EstimatedCostUsd.HasValue);
        return new ReviewScopeSpend(known, isApproximate);
    }

    private static async Task<ReviewScopeSpend> SumOtherJobCostAsync(
        MeisterProPRDbContext context,
        ReviewJob job,
        bool includeIncrementFilter,
        CancellationToken ct)
    {
        // Exclude the job itself: at job start its own cost has not accrued, and enforcement adds the live
        // in-run delta on top of this baseline, so counting it here would double-count. Prior attempts of the
        // same increment (a restart reuses the iteration on a new job row) remain counted, so their paid cost
        // is respected.
        var query = context.ReviewJobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.ClientId == job.ClientId &&
                candidate.OrganizationUrl == job.OrganizationUrl &&
                candidate.ProjectId == job.ProjectId &&
                candidate.RepositoryId == job.RepositoryId &&
                candidate.PullRequestId == job.PullRequestId &&
                candidate.Id != job.Id);

        if (includeIncrementFilter)
        {
            query = query.Where(candidate => candidate.IterationId == job.IterationId);
        }

        var rows = await query
            .Select(candidate => new CostProjection(candidate.TotalEstimatedCostUsd, candidate.CostIsApproximate))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var known = rows
            .Where(row => row.TotalEstimatedCostUsd.HasValue)
            .Sum(row => row.TotalEstimatedCostUsd!.Value);
        var isApproximate = rows.Any(row => !row.TotalEstimatedCostUsd.HasValue || row.CostIsApproximate);
        return new ReviewScopeSpend(known, isApproximate);
    }

    private sealed record CostProjection(decimal? TotalEstimatedCostUsd, bool CostIsApproximate);
}
