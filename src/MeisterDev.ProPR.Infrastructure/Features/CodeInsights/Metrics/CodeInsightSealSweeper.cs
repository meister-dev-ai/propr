// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Metrics;

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
            // Most recently active first: a pull request that closed last week is worth far more to a current
            // metric than one quiet for a year, and the ancient ones are leaving through retention anyway.
            .OrderByDescending(pullRequest => pullRequest.LastActivityAt)
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
                row.ClientId,
                row.RepositoryId,
                row.PullRequestId,
                jobByAggregate[row.Id]))
            .ToList();
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sealed {SealedCount} pull request(s) whose closure the synchronization path never observed.")]
    private static partial void LogSweepSealed(ILogger logger, int sealedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Selecting unsealed code-insight pull requests failed; the next sweep retries.")]
    private static partial void LogCandidateSelectionFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Asking the provider about PR {PullRequestId} (client {ClientId}) failed; it stays unmeasured.")]
    private static partial void LogExamineFailed(ILogger logger, long pullRequestId, Guid clientId, Exception ex);

    private readonly record struct Candidate(
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        Guid JobId);
}
