// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Intake.Persistence;

/// <summary>PostgreSQL projection for the client dashboard.</summary>
public sealed class EfCustomerDashboardReader(MeisterProPRDbContext dbContext) : ICustomerDashboardReader
{
    public async Task<CustomerDashboard> GetAsync(Guid clientId, DateTimeOffset windowEnd, CancellationToken ct)
    {
        var windowStart = windowEnd.AddDays(-30);
        var runningRows = await dbContext.ReviewJobs.AsNoTracking()
            .Where(job => job.ClientId == clientId && job.Status == JobStatus.Processing)
            .OrderByDescending(job => job.ProcessingStartedAt ?? job.SubmittedAt)
            .Select(job => new
            {
                job.Id,
                job.Status,
                job.Provider,
                job.PrRepositoryName,
                job.RepositoryId,
                job.PullRequestId,
                job.ProcessingStartedAt,
                job.SubmittedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        // The aggregate runs in PostgreSQL over the indexed client and completion window. Result JSON is
        // inspected in the database; neither review history nor finding text is transferred to this reader.
        var recentFindingCount = dbContext.Database.IsNpgsql()
            ? await dbContext.Database.SqlQuery<long>(
                $"""
                 SELECT COALESCE(SUM(
                     CASE WHEN jsonb_typeof(result_json -> 'Comments') = 'array'
                         THEN jsonb_array_length(result_json -> 'Comments') ELSE 0 END), 0)::bigint AS "Value"
                 FROM review_jobs
                 WHERE client_id = {clientId}
                   AND status = {nameof(JobStatus.Completed)}
                   AND completed_at >= {windowStart}
                   AND completed_at < {windowEnd}
                   AND result_json IS NOT NULL
                 """).SingleAsync(ct).ConfigureAwait(false)
            : (await dbContext.ReviewJobs.AsNoTracking()
                .Where(job => job.ClientId == clientId && job.Status == JobStatus.Completed &&
                              job.CompletedAt >= windowStart && job.CompletedAt < windowEnd)
                .Select(job => job.Result)
                .ToListAsync(ct).ConfigureAwait(false))
            .Sum(result => result?.Comments.Count ?? 0);

        return new CustomerDashboard(
            windowStart,
            windowEnd,
            recentFindingCount,
            runningRows.Select(job => new CustomerRunningReview(
                job.Id,
                job.Status,
                job.Provider,
                job.PrRepositoryName ?? job.RepositoryId,
                job.PullRequestId,
                job.ProcessingStartedAt ?? job.SubmittedAt)).ToArray());
    }
}
