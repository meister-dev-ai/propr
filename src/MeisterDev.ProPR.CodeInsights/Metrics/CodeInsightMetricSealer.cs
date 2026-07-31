// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>
///     Takes the one-time correctness measurement of a pull request when it finishes.
/// </summary>
/// <remarks>
///     <para>
///         Every close type (merged, abandoned, closed) seals identically. The state is recorded, but it does
///         not enter the computation: a finding the reviewer got right was right whether or not the pull request
///         was eventually merged.
///     </para>
///     <para>
///         The seal reads only what has already been decided. Dispositions exist only for findings whose threads
///         resolved, so counting them is inherently a count over the resolved set; the rest are counted once as
///         open-at-seal and otherwise ignored.
///     </para>
/// </remarks>
public sealed partial class CodeInsightMetricSealer(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightMetricSealer> logger,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightMetricSealer
{
    public async Task<bool> SealAsync(
        CodeInsightPullRequestKey key,
        string closeState,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            if (!await gate.IsCollectionEnabledAsync(key.ClientId, ct))
            {
                return false;
            }

            return await this.WithDbAsync(db => this.SealCoreAsync(db, key, closeState, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Two crawl passes can observe the same close at once, and the unique index on the aggregate lets
            // exactly one of them win; the loser lands here, which is the intended outcome rather than a fault.
            // Any other failure is swallowed for the same reason every collection side-write is: a measurement
            // is never worth disturbing the crawl that produced the facts.
            LogSealFailed(logger, key.PullRequestId, key.ClientId, ex);
            return false;
        }
    }

    private async Task<bool> SealCoreAsync(
        MeisterProPRDbContext db,
        CodeInsightPullRequestKey key,
        string closeState,
        CancellationToken ct)
    {
        var aggregate = await db.CodeInsightPullRequests
            .Where(candidate => candidate.ClientId == key.ClientId
                                && candidate.RepositoryId == key.RepositoryId
                                && candidate.PullRequestId == key.PullRequestId)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);

        if (aggregate is null)
        {
            // Nothing was collected for this pull request: the client had not opted in while it was open.
            return false;
        }

        var aggregateId = aggregate.Value;

        if (await db.CodeInsightPullRequestMetrics
                .AnyAsync(metric => metric.CodeInsightPullRequestId == aggregateId, ct))
        {
            // Already sealed. A reopen followed by another close does not get to move a number a report has
            // already shown.
            return false;
        }

        var findingIds = await db.CodeInsightFindings
            .Where(finding => finding.CodeInsightPullRequestId == aggregateId)
            .Select(finding => finding.Id)
            .ToListAsync(ct);

        List<CodeInsightDisposition> dispositions = [];
        if (findingIds.Count > 0)
        {
            dispositions = await db.CodeInsightFindingDispositions
                .Where(disposition => findingIds.Contains(disposition.CodeInsightFindingId))
                .Select(disposition => disposition.Disposition)
                .ToListAsync(ct);
        }

        // Only the qualifying ones. The others were harvested to make the cut-off inspectable, and counting
        // them would charge the reviewer for questions and nits it was right to leave alone.
        var misses = await db.CodeInsightMisses
            .CountAsync(miss => miss.CodeInsightPullRequestId == aggregateId && miss.CountsAsMiss, ct);

        var inputs = new CodeInsightMetricInputs(
            dispositions.Count(disposition => disposition == CodeInsightDisposition.Addressed),
            dispositions.Count(disposition => disposition == CodeInsightDisposition.Acknowledged),
            dispositions.Count(disposition => disposition == CodeInsightDisposition.Dismissed),
            dispositions.Count(disposition => disposition == CodeInsightDisposition.FalsePositive),
            misses,
            dispositions.Count(disposition => disposition == CodeInsightDisposition.Discussed));

        if (inputs.Resolved == 0 && inputs.Misses == 0)
        {
            // There is nothing to measure: no finding reached an outcome and no miss was harvested. A row of
            // undefined ratios would still count toward the sample size of every metric it says nothing about,
            // making a period look better evidenced than it is. Leaving it unsealed also keeps the door open for
            // a genuine first measurement if this pull request is closed again with outcomes recorded.
            LogNothingToMeasure(logger, key.PullRequestId, key.ClientId);
            return false;
        }

        var metrics = CodeInsightMetricCalculator.Compute(inputs);
        var sealedAt = DateTimeOffset.UtcNow;

        db.CodeInsightPullRequestMetrics.Add(
            new CodeInsightPullRequestMetric
            {
                Id = Guid.CreateVersion7(),
                CodeInsightPullRequestId = aggregateId,
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                AddressedCount = inputs.Addressed,
                AcknowledgedCount = inputs.Acknowledged,
                DismissedCount = inputs.Dismissed,
                FalsePositiveCount = inputs.FalsePositive,
                DiscussedCount = inputs.Discussed,
                MissCount = inputs.Misses,
                ResolvedCount = inputs.Resolved,
                OpenAtSealCount = findingIds.Count - dispositions.Count,
                Precision = metrics.Precision,
                Recall = metrics.Recall,
                F1 = metrics.F1,
                AcceptanceRate = metrics.AcceptanceRate,
                CloseState = closeState,
                SealedAt = sealedAt,
                SealedOn = DateOnly.FromDateTime(sealedAt.UtcDateTime),
            });

        await db.SaveChangesAsync(ct);
        LogSealed(logger, key.PullRequestId, key.ClientId, inputs.Resolved, inputs.Misses);
        return true;
    }

    private async Task<bool> WithDbAsync(Func<MeisterProPRDbContext, Task<bool>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }
}
