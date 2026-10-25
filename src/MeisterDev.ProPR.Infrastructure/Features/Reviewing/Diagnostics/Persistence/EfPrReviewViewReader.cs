// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

/// <summary>Projects the database fields displayed by the pull request review view.</summary>
public sealed class EfPrReviewViewReader(MeisterProPRDbContext db) : IPrReviewViewReader
{
    private const int ThreadPassLimit = 50;

    /// <inheritdoc />
    public async Task<PrReviewViewData> ReadAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var matchingJobs = db.ReviewJobs.AsNoTracking().Where(job =>
            job.ClientId == clientId && job.OrganizationUrl == providerScopePath &&
            job.ProjectId == providerProjectKey && job.RepositoryId == repositoryId && job.PullRequestId == pullRequestId);
        var skip = (page - 1) * pageSize;
        var jobs = db.Database.IsNpgsql()
            ? await this.ReadPostgresJobsAsync(
                clientId, providerScopePath, providerProjectKey, repositoryId,
                pullRequestId, skip, pageSize, ct).ConfigureAwait(false)
            : await matchingJobs.OrderByDescending(job => job.SubmittedAt).ThenBy(job => job.Id)
                .Skip(skip).Take(pageSize)
                .Select(job => new PrJobSummaryDto(
                    job.Id, job.Status, job.SubmittedAt, job.CompletedAt,
                    job.Result == null ? null : job.Result.Comments.Count,
                    job.TotalInputTokensAggregated ?? job.Protocols.Sum(protocol => protocol.TotalInputTokens) ?? 0L,
                    job.TotalOutputTokensAggregated ?? job.Protocols.Sum(protocol => protocol.TotalOutputTokens) ?? 0L,
                    job.TokenBreakdown, job.TotalEstimatedCostUsd, job.CostIsApproximate))
                .ToListAsync(ct).ConfigureAwait(false);

        var payloads = await this.ReadContributionPayloadsAsync(matchingJobs, jobs, ct).ConfigureAwait(false);
        var threadPasses = await db.ThreadPassJobs.AsNoTracking()
            .Where(pass => pass.ClientId == clientId && pass.OrganizationUrl == providerScopePath &&
                           pass.ProjectId == providerProjectKey && pass.RepositoryId == repositoryId && pass.PullRequestId == pullRequestId)
            .OrderByDescending(pass => pass.CreatedAt).ThenBy(pass => pass.Id)
            .Take(ThreadPassLimit)
            .Select(pass => new PrThreadPassSummaryDto(
                pass.Id, pass.Status, pass.CreatedAt, pass.CompletedAt, pass.HandledThreads.Count,
                pass.TotalInputTokens, pass.TotalOutputTokens, pass.TotalEstimatedCostUsd, pass.CostIsApproximate,
                pass.ErrorMessage, pass.BudgetBlockScope, pass.BudgetBlockCapKind, pass.BudgetBlockThresholdUsd, pass.BudgetBlockSpentUsd))
            .ToListAsync(ct).ConfigureAwait(false);
        var watermark = await db.ReviewPrScans.AsNoTracking()
            .Where(scan => scan.ClientId == clientId && scan.OrganizationUrl == providerScopePath &&
                           scan.ProjectId == providerProjectKey && scan.RepositoryId == repositoryId && scan.PullRequestId == pullRequestId)
            .Select(scan => new PrReviewWatermark(scan.PendingReviewRevisionKey, scan.LastProcessedCommitId, scan.PendingReviewDetectedAt))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new PrReviewViewData(jobs, payloads, threadPasses, watermark);
    }

    private async Task<List<PrJobSummaryDto>> ReadPostgresJobsAsync(
        Guid clientId, string scope, string project, string repository, int pullRequest, int skip, int pageSize, CancellationToken ct)
    {
        // Result is stored through a value converter. Count its comments in PostgreSQL so the complete
        // result and its evidence are not transferred or deserialized for this summary.
        var rows = await db.Database.SqlQuery<JobSummaryRow>(
            $"""
             SELECT j.id AS "Id", j.status AS "Status", j.submitted_at AS "SubmittedAt", j.completed_at AS "CompletedAt",
                    CASE WHEN j.result_json IS NULL OR j.result_json = 'null'::jsonb THEN NULL
                         WHEN jsonb_typeof(j.result_json -> 'Comments') = 'array'
                         THEN jsonb_array_length(j.result_json -> 'Comments') ELSE 0 END AS "FindingCount",
                    COALESCE(j.total_input_tokens_aggregated,
                        (SELECT SUM(p.total_input_tokens)::bigint FROM review_job_protocols p WHERE p.job_id = j.id), 0) AS "TotalInputTokens",
                    COALESCE(j.total_output_tokens_aggregated,
                        (SELECT SUM(p.total_output_tokens)::bigint FROM review_job_protocols p WHERE p.job_id = j.id), 0) AS "TotalOutputTokens",
                    j.token_breakdown::text AS "TokenBreakdownJson", j.total_estimated_cost_usd AS "TotalEstimatedCostUsd",
                    j.cost_is_approximate AS "CostIsApproximate"
             FROM review_jobs j
             WHERE j.client_id = {clientId} AND j.organization_url = {scope} AND j.project_id = {project}
               AND j.repository_id = {repository} AND j.pull_request_id = {pullRequest}
             ORDER BY j.submitted_at DESC, j.id
             LIMIT {pageSize} OFFSET {skip}
             """).ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(row => new PrJobSummaryDto(
            row.Id, Enum.Parse<JobStatus>(row.Status), row.SubmittedAt, row.CompletedAt, row.FindingCount,
            row.TotalInputTokens, row.TotalOutputTokens, DeserializeTokenBreakdown(row.TokenBreakdownJson),
            row.TotalEstimatedCostUsd, row.CostIsApproximate)).ToList();
    }

    private async Task<IReadOnlyList<string>> ReadContributionPayloadsAsync(
        IQueryable<ReviewJob> matchingJobs, IReadOnlyList<PrJobSummaryDto> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0)
        {
            return [];
        }

        var jobIds = jobs.Select(job => job.JobId).ToArray();
        var samples = await (
                from job in matchingJobs
                where jobIds.Contains(job.Id)
                from protocol in job.Protocols
                from ev in protocol.Events
                where ev.Name == ReviewProtocolEventNames.MemoryReconsiderationCompleted && ev.InputTextSample != null
                orderby protocol.AttemptNumber descending, protocol.Id, ev.OccurredAt
                select new { JobId = job.Id, Sample = ev.InputTextSample! })
            .ToListAsync(ct).ConfigureAwait(false);

        var pageOrder = jobs.Select((job, index) => (job.JobId, index)).ToDictionary(item => item.JobId, item => item.index);
        return samples.OrderBy(sample => pageOrder[sample.JobId]).Select(sample => sample.Sample).ToList();
    }

    private static IReadOnlyList<TokenBreakdownEntry> DeserializeTokenBreakdown(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TokenBreakdownEntry>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class JobSummaryRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset SubmittedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int? FindingCount { get; set; }
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public string TokenBreakdownJson { get; set; } = "[]";
        public decimal? TotalEstimatedCostUsd { get; set; }
        public bool CostIsApproximate { get; set; }
    }
}
