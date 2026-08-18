// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Persistence;

/// <summary>
///     Counts installation activity for one window, from the tables the product already keeps.
///     <para>
///         Every query is unscoped by tenant and by client, so the counts are installation-wide totals. The
///         payload has no field that could carry a per-tenant or per-client breakdown.
///     </para>
/// </summary>
public sealed class UsageStatisticsCountRepository(MeisterProPRDbContext dbContext) : IUsageStatisticsCountSource
{
    /// <inheritdoc />
    public async Task<UsageStatisticsCounts> CountAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default)
    {
        var activeUsers = await dbContext.AppUsers
            .AsNoTracking()
            .CountAsync(user => user.IsActive, cancellationToken);

        var pullRequests = await this.CountReviewedPullRequestsAsync(windowStart, windowEnd, cancellationToken);

        var findingsRaised = await dbContext.PostedFindingRecords
            .AsNoTracking()
            .CountAsync(
                record => record.CreatedAt >= windowStart && record.CreatedAt < windowEnd,
                cancellationToken);

        var (accepted, dismissed) = await this.CountFindingOutcomesAsync(windowStart, windowEnd, cancellationToken);

        return new UsageStatisticsCounts(activeUsers, pullRequests, findingsRaised, accepted, dismissed);
    }

    /// <summary>
    ///     Counts distinct pull requests reviewed, rather than review jobs run.
    ///     <para>
    ///         A pull request reviewed three times incrementally counts once. Counting jobs would report a team
    ///         that pushes often as a larger installation.
    ///     </para>
    ///     <para>
    ///         The key is the client, the repository and the pull request number. It excludes the provider and
    ///         the provider-neutral review identifier, because both are seeded with a default and reassigned
    ///         once a job resolves its normalized context; a pull request reviewed either side of that point
    ///         would split into two keys and count twice.
    ///     </para>
    /// </summary>
    private Task<int> CountReviewedPullRequestsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        return dbContext.ReviewJobs
            .AsNoTracking()
            .Where(job =>
                job.Status == JobStatus.Completed
                && job.CompletedAt >= windowStart
                && job.CompletedAt < windowEnd)
            .Select(job => new
            {
                job.ClientId,
                job.RepositoryId,
                job.PullRequestId,
            })
            .Distinct()
            .CountAsync(cancellationToken);
    }

    /// <summary>
    ///     Counts accepted and dismissed findings, or reports that this installation records no outcomes.
    ///     <para>
    ///         An outcome is only known where code-insight collection has produced one. Elsewhere both counters
    ///         are absent from the payload rather than reported as zero, because a zero would read as "nothing
    ///         was accepted" and would lower the fleet-wide ratio with installations that never measured it.
    ///     </para>
    ///     <para>
    ///         The condition is whether any outcome has ever been recorded, not whether a client has the
    ///         setting switched on. The setting is a point-in-time flag, so testing it would report zero for
    ///         the first week after an operator enables collection, which is the reading the absent field
    ///         avoids.
    ///     </para>
    /// </summary>
    private async Task<(int? Accepted, int? Dismissed)> CountFindingOutcomesAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var recordsOutcomes = await dbContext.CodeInsightFindingDispositions
            .AsNoTracking()
            .AnyAsync(cancellationToken);

        if (!recordsOutcomes)
        {
            return (null, null);
        }

        // Accepted covers both ways an author can agree with a finding: fixing it, or acknowledging it without
        // a change in this pull request. Dismissed is the "correct but unwanted" outcome, kept separate from a
        // false positive so the two are not conflated in the fleet-wide ratio.
        //
        // One grouped pass rather than two counts, because each pass joins the two largest code-insight
        // tables.
        var byDisposition = await (
                from disposition in dbContext.CodeInsightFindingDispositions.AsNoTracking()
                join finding in dbContext.CodeInsightFindings.AsNoTracking()
                    on disposition.CodeInsightFindingId equals finding.Id
                where finding.ObservedAt >= windowStart && finding.ObservedAt < windowEnd
                group disposition by disposition.Disposition
                into grouped
                select new { Disposition = grouped.Key, Count = grouped.Count() })
            .ToListAsync(cancellationToken);

        var accepted = byDisposition
            .Where(entry => entry.Disposition is CodeInsightDisposition.Addressed
                or CodeInsightDisposition.Acknowledged)
            .Sum(entry => entry.Count);

        var dismissed = byDisposition
            .Where(entry => entry.Disposition == CodeInsightDisposition.Dismissed)
            .Sum(entry => entry.Count);

        return (accepted, dismissed);
    }
}
