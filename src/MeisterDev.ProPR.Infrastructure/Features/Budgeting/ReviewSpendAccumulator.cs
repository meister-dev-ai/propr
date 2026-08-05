// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Computes accumulated spend per budget scope from the persisted per-job USD cost
///     (<see cref="ReviewJob.TotalEstimatedCostUsd" /> and <see cref="ThreadPassJob.TotalEstimatedCostUsd" />) and
///     the per-client daily usage samples. All sums are null-aware: an unpriced contribution is omitted from the
///     total and flags the scope approximate rather than being coerced to zero.
/// </summary>
/// <remarks>
///     The pull-request and increment scopes total both kinds of job. A thread pass spends a client's money
///     against the same pull request a review does, so a scope that saw only review jobs would let a recurring,
///     deliberately created category of spend run past every cap. The client month-to-date scope needs no such
///     widening: it reads the daily usage samples, which both kinds of job write.
/// </remarks>
public sealed class ReviewSpendAccumulator(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IClientTokenUsageRepository usageRepository) : IReviewSpendAccumulator
{
    /// <inheritdoc />
    public async Task<ReviewSpendBaseline> GetBaselineAsync(
        ReviewSpendSubject subject,
        DateOnly asOfDate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var clientMonthToDate = await this.SumClientMonthToDateAsync(subject.ClientId, asOfDate, ct).ConfigureAwait(false);

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pullRequest = await SumOtherUnitCostAsync(context, subject, includeIncrementFilter: false, ct).ConfigureAwait(false);
        var increment = await SumOtherUnitCostAsync(context, subject, includeIncrementFilter: true, ct).ConfigureAwait(false);

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

    private static async Task<ReviewScopeSpend> SumOtherUnitCostAsync(
        MeisterProPRDbContext context,
        ReviewSpendSubject subject,
        bool includeIncrementFilter,
        CancellationToken ct)
    {
        // Exclude the subject itself: at its start its own cost has not accrued, and enforcement adds the live
        // in-run delta on top of this baseline, so counting it here would double-count. Prior attempts of the
        // same increment (a restart reuses the iteration on a new job row) remain counted, so their paid cost
        // is respected.
        var reviewJobs = context.ReviewJobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.ClientId == subject.ClientId &&
                candidate.OrganizationUrl == subject.OrganizationUrl &&
                candidate.ProjectId == subject.ProjectId &&
                candidate.RepositoryId == subject.RepositoryId &&
                candidate.PullRequestId == subject.PullRequestId &&
                candidate.Id != subject.UnitOfWorkId);

        var threadPasses = context.ThreadPassJobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.ClientId == subject.ClientId &&
                candidate.OrganizationUrl == subject.OrganizationUrl &&
                candidate.ProjectId == subject.ProjectId &&
                candidate.RepositoryId == subject.RepositoryId &&
                candidate.PullRequestId == subject.PullRequestId &&
                candidate.Id != subject.UnitOfWorkId);

        if (includeIncrementFilter)
        {
            reviewJobs = reviewJobs.Where(candidate => candidate.IterationId == subject.IterationId);
            threadPasses = threadPasses.Where(candidate => candidate.IterationId == subject.IterationId);
        }

        var rows = await reviewJobs
            .Select(candidate => new CostProjection(candidate.TotalEstimatedCostUsd, candidate.CostIsApproximate))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // A pass that has not yet spent anything reports no cost at all, which is silence rather than an
        // unpriced call, so it must not flag the scope approximate the way an unpriced review job does.
        var passRows = await threadPasses
            .Where(candidate => candidate.TotalEstimatedCostUsd != null || candidate.CostIsApproximate)
            .Select(candidate => new CostProjection(candidate.TotalEstimatedCostUsd, candidate.CostIsApproximate))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var known = rows
            .Where(row => row.TotalEstimatedCostUsd.HasValue)
            .Sum(row => row.TotalEstimatedCostUsd!.Value);
        known += passRows
            .Where(row => row.TotalEstimatedCostUsd.HasValue)
            .Sum(row => row.TotalEstimatedCostUsd!.Value);

        var isApproximate = rows.Any(row => !row.TotalEstimatedCostUsd.HasValue || row.CostIsApproximate)
                            || passRows.Any(row => row.CostIsApproximate);
        return new ReviewScopeSpend(known, isApproximate);
    }

    private sealed record CostProjection(decimal? TotalEstimatedCostUsd, bool CostIsApproximate);
}
