// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Application.Features.CodeInsights.Survival;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Survival;

/// <summary>
///     Reads finding survival: of the problems a review raised, how many were still being raised at the pull
///     request's newest increment.
/// </summary>
/// <remarks>
///     <para>
///         Grouped by chain, because a problem re-reported across three increments is three rows and one problem.
///         A chain whose newest row carries the pull request's newest revision key was still standing; one whose
///         newest row is older stopped being reported.
///     </para>
///     <para>
///         A pull request with a single increment contributes nothing. Every chain is trivially at the newest
///         revision there, so counting it would report perfect persistence for a pull request that never had the
///         chance to shed anything, and reviewed-once pull requests would then drown the signal.
///     </para>
/// </remarks>
public sealed class CodeInsightSurvivalReader(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightSurvivalReader
{
    public async Task<CodeInsightSurvivalCounts> GetSurvivalAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var perPullRequest = await this.LoadAsync(query, ct);

        return perPullRequest.Aggregate(
            default(CodeInsightSurvivalCounts),
            (total, row) => new CodeInsightSurvivalCounts(
                total.Persisted + row.Counts.Persisted,
                total.Fixed + row.Counts.Fixed,
                total.Dropped + row.Counts.Dropped,
                total.PullRequests + 1));
    }

    public async Task<IReadOnlyList<CodeInsightPullRequestSurvival>> GetSurvivalByPullRequestAsync(
        CodeInsightRollupQuery query,
        int topN,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var perPullRequest = await this.LoadAsync(query, ct);

        return perPullRequest
            // Most shed first: a pull request whose findings evaporated is the one worth reading.
            .OrderByDescending(row => row.Counts.Dropped)
            .ThenByDescending(row => row.Counts.Total)
            .ThenBy(row => row.PullRequestId)
            .Take(topN)
            .ToList();
    }

    private Task<List<CodeInsightPullRequestSurvival>> LoadAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct)
    {
        return this.WithDbAsync(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return new List<CodeInsightPullRequestSurvival>();
                }

                var scopes = await LoadScopesAsync(db, query, ct);
                if (scopes.Count == 0)
                {
                    return new List<CodeInsightPullRequestSurvival>();
                }

                var (from, toExclusive) = Window(query);
                var aggregateIds = scopes.Select(scope => scope.Id).ToList();

                var findings = await db.CodeInsightFindings
                    .AsNoTracking()
                    .Where(finding => aggregateIds.Contains(finding.CodeInsightPullRequestId))
                    .Where(finding => finding.ObservedAt >= from && finding.ObservedAt < toExclusive)
                    .Select(finding => new
                    {
                        finding.Id,
                        finding.CodeInsightPullRequestId,
                        finding.FindingChainId,
                        finding.RevisionKey,
                        finding.ObservedAt,
                    })
                    .ToListAsync(ct);

                if (findings.Count == 0)
                {
                    return new List<CodeInsightPullRequestSurvival>();
                }

                // A corroborated fix is what separates "the reviewer worked" from "it stopped saying it".
                var findingIds = findings.Select(finding => finding.Id).ToList();
                var addressed = (await db.CodeInsightFindingDispositions
                        .AsNoTracking()
                        .Where(disposition => findingIds.Contains(disposition.CodeInsightFindingId)
                                              && disposition.Disposition == CodeInsightDisposition.Addressed)
                        .Select(disposition => disposition.CodeInsightFindingId)
                        .ToListAsync(ct))
                    .ToHashSet();

                var byAggregate = findings
                    .GroupBy(finding => finding.CodeInsightPullRequestId)
                    .ToDictionary(group => group.Key, group => group.ToList());

                var results = new List<CodeInsightPullRequestSurvival>();

                foreach (var scope in scopes)
                {
                    if (!byAggregate.TryGetValue(scope.Id, out var own))
                    {
                        continue;
                    }

                    var revisions = own.Select(finding => finding.RevisionKey).Distinct(StringComparer.Ordinal).Count();
                    if (revisions < 2)
                    {
                        // Nothing had the chance to be dropped, so it says nothing about durability.
                        continue;
                    }

                    var persisted = 0;
                    var resolved = 0;
                    var dropped = 0;

                    foreach (var chain in own.GroupBy(finding => finding.FindingChainId))
                    {
                        var newest = chain
                            .OrderByDescending(finding => finding.ObservedAt)
                            .ThenByDescending(finding => finding.Id)
                            .First();

                        if (string.Equals(newest.RevisionKey, scope.LatestRevisionKey, StringComparison.Ordinal))
                        {
                            persisted++;
                        }
                        else if (chain.Any(finding => addressed.Contains(finding.Id)))
                        {
                            resolved++;
                        }
                        else
                        {
                            dropped++;
                        }
                    }

                    results.Add(
                        new CodeInsightPullRequestSurvival(
                            scope.ClientId,
                            scope.RepositoryId,
                            scope.PullRequestId,
                            revisions,
                            new CodeInsightSurvivalCounts(persisted, resolved, dropped, 1),
                            scope.RepositoryName));
                }

                return results;
            },
            ct);
    }

    /// <summary>
    ///     Resolves the pull-request aggregates in scope. The client filter is unconditional, like every other
    ///     code-insight read; aggregates with no recorded newest increment are skipped because there is nothing to
    ///     judge a chain against.
    /// </summary>
    private static async Task<List<ScopeRow>> LoadScopesAsync(
        MeisterProPRDbContext db,
        CodeInsightRollupQuery query,
        CancellationToken ct)
    {
        var clientIds = query.ClientIds.ToList();
        var aggregates = db.CodeInsightPullRequests
            .AsNoTracking()
            .Where(pullRequest => clientIds.Contains(pullRequest.ClientId))
            .Where(pullRequest => pullRequest.LatestRevisionKey != string.Empty);

        if (query.RepositoryId is not null)
        {
            aggregates = aggregates.Where(pullRequest => pullRequest.RepositoryId == query.RepositoryId);
        }

        if (query.PullRequestId is not null)
        {
            aggregates = aggregates.Where(pullRequest => pullRequest.PullRequestId == query.PullRequestId.Value);
        }

        var rows = await aggregates
            .Select(pullRequest => new
            {
                pullRequest.Id,
                pullRequest.ClientId,
                pullRequest.RepositoryId,
                pullRequest.PullRequestId,
                pullRequest.LatestRevisionKey,
                pullRequest.RepositoryName,
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new ScopeRow(
                row.Id,
                row.ClientId,
                row.RepositoryId,
                row.PullRequestId,
                row.LatestRevisionKey,
                row.RepositoryName))
            .ToList();
    }

    /// <summary>
    ///     Turns the inclusive date window into a half-open instant range, so a review that ran late on the last
    ///     day of the window is inside it.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset ToExclusive) Window(CodeInsightRollupQuery query)
    {
        return (
            new DateTimeOffset(query.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(query.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
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

    private readonly record struct ScopeRow(
        Guid Id,
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        string LatestRevisionKey,
        string? RepositoryName);
}
