// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Intake.Persistence;

/// <summary>Projects client history without transferring result JSON or protocol events.</summary>
public sealed class EfCustomerReviewHistoryReader(MeisterProPRDbContext db) : ICustomerReviewHistoryReader
{
    /// <inheritdoc />
    public async Task<CustomerReviewHistory> GetAsync(Guid clientId, int page, int pageSize, JobStatus? status, CancellationToken ct)
    {
        ValidateArguments(page, pageSize, status);
        var offset = (page - 1) * pageSize;

        if (!db.Database.IsNpgsql())
        {
            throw new NotSupportedException("Customer review history requires PostgreSQL.");
        }

        var jobs = db.ReviewJobs.AsNoTracking().Where(job => job.ClientId == clientId);
        if (status.HasValue)
        {
            jobs = jobs.Where(job => job.Status == status.Value);
        }

        var count = await jobs.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await this.ReadPageAsync(clientId, pageSize, offset, status, ct).ConfigureAwait(false);
        var items = rows.Select(MapItem).ToArray();
        return new CustomerReviewHistory(count, page, pageSize, items);
    }

    private static void ValidateArguments(int page, int pageSize, JobStatus? status)
    {
        if (page < 1 || pageSize is < 1 or > 100 || page > int.MaxValue / pageSize ||
            (status.HasValue && !Enum.IsDefined(status.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }
    }

    private async Task<List<HistoryRow>> ReadPageAsync(Guid clientId, int pageSize, int offset, JobStatus? status, CancellationToken ct)
    {
        var statusName = status?.ToString();
        return await db.Database.SqlQuery<HistoryRow>(
            $"""
             SELECT id AS "Id", status AS "Status", provider AS "Provider",
                    COALESCE(pr_repository_name, repository_id) AS "Repository",
                    pull_request_id AS "PullRequestNumber", submitted_at AS "SubmittedAt",
                    completed_at AS "CompletedAt",
                    CASE WHEN jsonb_typeof(result_json -> 'Comments') = 'array'
                         THEN jsonb_array_length(result_json -> 'Comments') ELSE 0 END AS "FindingCount"
             FROM review_jobs
             WHERE client_id = {clientId} AND ({statusName}::text IS NULL OR status = {statusName})
             ORDER BY submitted_at DESC, id DESC
             LIMIT {pageSize} OFFSET {offset}
             """).ToListAsync(ct).ConfigureAwait(false);
    }

    private static CustomerReviewHistoryItem MapItem(HistoryRow row) =>
        new(
            row.Id,
            Enum.Parse<JobStatus>(row.Status),
            (ScmProvider)row.Provider,
            row.Repository,
            row.PullRequestNumber,
            row.SubmittedAt,
            row.CompletedAt,
            row.FindingCount);

    private sealed class HistoryRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "";
        public int Provider { get; set; }
        public string Repository { get; set; } = "";
        public int PullRequestNumber { get; set; }
        public DateTimeOffset SubmittedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int FindingCount { get; set; }
    }
}
