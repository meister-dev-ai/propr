// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>
///     Finds collected pull requests that have gone quiet without ever being measured, asks the provider whether
///     they finished, and seals the ones that did.
/// </summary>
/// <remarks>
///     <para>
///         The provider is asked through the lightweight reference fetch, which is provider-neutral and returns the
///         pull request's status without downloading any file content. The heavier fetch would multiply request
///         load for data this sweep does not need.
///     </para>
///     <para>
///         The provider scope a fetch needs (the organisation or host path and the project key) is not on the
///         code-insight aggregate, which deliberately knows only (client, repository, pull request). It is read
///         from the review job that produced the findings, which is the same job identity the aggregate already
///         records for every finding.
///     </para>
/// </remarks>
public sealed partial class CodeInsightSealSweeper(
    MeisterProPRDbContext dbContext,
    ICodeInsightMetricSealer sealer,
    ICodeInsightsCollectionGate gate,
    IJobRepository jobRepository,
    ILogger<CodeInsightSealSweeper> logger,
    IPullRequestFetcher? pullRequestFetcher = null,
    ICodeInsightCloseObserver? closeObserver = null,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightSealSweeper
{
    public async Task<int> SweepAsync(
        int maxPullRequests,
        TimeSpan idleFor,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPullRequests);

        if (pullRequestFetcher is null)
        {
            // No provider adapter registered: a database-less or offline installation. Nothing to ask.
            return 0;
        }

        List<Candidate> candidates;

        try
        {
            candidates = await this.WithDbAsync(
                db => this.FindCandidatesAsync(db, maxPullRequests, idleFor, ct),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCandidateSelectionFailed(logger, ex);
            return 0;
        }

        var sealedCount = 0;

        foreach (var candidate in candidates)
        {
            sealedCount += await this.ExamineAsync(candidate, ct) ? 1 : 0;

            // Stamped whatever the outcome was, so the queue rotates. A sealed aggregate is excluded from the
            // candidate set by its metric row anyway; the stamp is what keeps the unsealed ones moving.
            await this.RecordAttemptAsync(candidate.AggregateId, ct);
        }

        if (sealedCount > 0)
        {
            LogSweepSealed(logger, sealedCount);
        }

        return sealedCount;
    }

    /// <summary>
    ///     Asks the provider about one candidate and seals it when the pull request is no longer active.
    /// </summary>
    private async Task<bool> ExamineAsync(Candidate candidate, CancellationToken ct)
    {
        try
        {
            var job = jobRepository.GetById(candidate.JobId);
            if (job is null)
            {
                // The review job is gone, so the provider scope cannot be resolved. Nothing to ask and nothing to
                // invent: an unmeasured pull request is better than one measured against a guess.
                return false;
            }

            var reference = await pullRequestFetcher!.FetchRefAsync(
                job.OrganizationUrl,
                job.ProjectId,
                candidate.RepositoryId,
                (int)candidate.PullRequestId,
                candidate.ClientId,
                ct);

            if (reference.Status == PrStatus.Active)
            {
                // Still open. The provider also reports Active for a transient failure, which is the safe answer
                // here: a measurement postponed is recoverable, one sealed against a wrong status is not.
                return false;
            }

            // This pull request closed without any pass observing it, so its human threads were last judged
            // while they were open, before the acted-on question could have an answer. Observing now is the one
            // opportunity to revise those judgements: the seal below counts what is already recorded, and it
            // never moves once written.
            if (closeObserver is not null)
            {
                try
                {
                    await closeObserver.ObserveAsync(
                        new CodeInsightPullRequestKey(candidate.ClientId, candidate.RepositoryId, candidate.PullRequestId),
                        job.OrganizationUrl,
                        job.ProjectId,
                        ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The observer already swallows its own failures. Catching again here keeps a failed
                    // observation from costing the measurement as well, which the enclosing catch would do by
                    // returning without sealing.
                    LogObservationFailed(logger, candidate.PullRequestId, candidate.ClientId, ex);
                }
            }

            return await sealer.SealAsync(
                new CodeInsightPullRequestKey(candidate.ClientId, candidate.RepositoryId, candidate.PullRequestId),
                reference.Status.ToString(),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreachable pull request must not end the sweep for the rest.
            LogExamineFailed(logger, candidate.PullRequestId, candidate.ClientId, ex);
            return false;
        }
    }

    /// <summary>
    ///     Selects unmeasured, quiet pull requests belonging to clients whose collection gate is open, most
    ///     recently active first.
    /// </summary>
    private async Task<List<Candidate>> FindCandidatesAsync(
        MeisterProPRDbContext db,
        int maxPullRequests,
        TimeSpan idleFor,
        CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - idleFor;

        var clientIds = await db.CodeInsightPullRequests
            .Where(pullRequest => pullRequest.LastActivityAt < cutoff)
            .Select(pullRequest => pullRequest.ClientId)
            .Distinct()
            .ToListAsync(ct);

        var open = new List<Guid>();
        foreach (var clientId in clientIds)
        {
            if (await gate.IsCollectionEnabledAsync(clientId, ct))
            {
                open.Add(clientId);
            }
        }

        if (open.Count == 0)
        {
            return [];
        }

        var quiet = await db.CodeInsightPullRequests
            .Where(pullRequest => open.Contains(pullRequest.ClientId))
            .Where(pullRequest => pullRequest.LastActivityAt < cutoff)
            .Where(pullRequest => !db.CodeInsightPullRequestMetrics
                .Any(metric => metric.CodeInsightPullRequestId == pullRequest.Id))
            // An aggregate with no finding has no review job to resolve the provider scope from, so it can never
            // produce a seal. Excluded here, before the cap, so the cap is spent on rows that can.
            .Where(pullRequest => db.CodeInsightFindings
                .Any(finding => finding.CodeInsightPullRequestId == pullRequest.Id))
            // Least recently attempted first, never-attempted before that. Ordering by activity alone would
            // re-present the same newest rows on every cycle, so anything below the cap when it went quiet would
            // sink further with every sweep and never be examined. Activity remains the tiebreaker, which keeps
            // the original intent that a pull request quiet for a week matters more than one quiet for a year.
            .OrderBy(pullRequest => pullRequest.LastSealAttemptAt == null ? 0 : 1)
            .ThenBy(pullRequest => pullRequest.LastSealAttemptAt)
            .ThenByDescending(pullRequest => pullRequest.LastActivityAt)
            .Select(pullRequest => new
            {
                pullRequest.Id,
                pullRequest.ClientId,
                pullRequest.RepositoryId,
                pullRequest.PullRequestId,
            })
            .Take(maxPullRequests)
            .ToListAsync(ct);

        if (quiet.Count == 0)
        {
            return [];
        }

        var aggregateIds = quiet.Select(row => row.Id).ToList();

        // One job per aggregate is enough: the provider scope is a property of where the pull request lives, not
        // of which review looked at it.
        var jobByAggregate = await db.CodeInsightFindings
            .Where(finding => aggregateIds.Contains(finding.CodeInsightPullRequestId))
            .GroupBy(finding => finding.CodeInsightPullRequestId)
            .Select(group => new { AggregateId = group.Key, JobId = group.Min(finding => finding.JobId) })
            .ToDictionaryAsync(row => row.AggregateId, row => row.JobId, ct);

        return quiet
            .Where(row => jobByAggregate.ContainsKey(row.Id))
            .Select(row => new Candidate(
                row.Id,
                row.ClientId,
                row.RepositoryId,
                row.PullRequestId,
                jobByAggregate[row.Id]))
            .ToList();
    }

    /// <summary>
    ///     Stamps the attempt on the aggregate so the next sweep orders this candidate behind the ones it has not
    ///     reached yet.
    /// </summary>
    /// <remarks>
    ///     Written for every examined candidate, including the ones that did not seal. A pull request still open at
    ///     the provider, one whose review job is gone, and one the provider could not be reached about all leave
    ///     no measurement behind, and without a stamp they would be indistinguishable from a candidate that has
    ///     never been examined. Failing to record the attempt is not worth failing the sweep over: the cost is one
    ///     wasted slot on the next cycle.
    /// </remarks>
    private async Task RecordAttemptAsync(Guid aggregateId, CancellationToken ct)
    {
        try
        {
            await this.WithDbAsync<bool>(
                async db =>
                {
                    var aggregate = await db.CodeInsightPullRequests
                        .FirstOrDefaultAsync(candidate => candidate.Id == aggregateId, ct);
                    if (aggregate is null)
                    {
                        return false;
                    }

                    aggregate.LastSealAttemptAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return true;
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogAttemptNotRecorded(logger, aggregateId, ex);
        }
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    private readonly record struct Candidate(
        Guid AggregateId,
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        Guid JobId);
}
