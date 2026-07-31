// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.History;

/// <summary>
///     What to compare review history against the collection over.
/// </summary>
/// <param name="ClientIds">Clients the caller may see. An empty list reads nothing.</param>
/// <param name="From">Inclusive start of the window, by review submission date.</param>
/// <param name="To">Inclusive end of the window, by review submission date.</param>
public sealed record CodeInsightHistoryCoverageQuery(
    IReadOnlyList<Guid> ClientIds,
    DateOnly From,
    DateOnly To);

/// <summary>
///     One repository's answer to "how much of what has already been reviewed does the collection know about".
/// </summary>
/// <remarks>
///     Collection starts the day it is switched on, so every metric on the reviewer-performance surface is blind
///     to reviews that ran before it. These counts say how blind, per repository, in the units an import works
///     in: review jobs and pull requests.
/// </remarks>
/// <param name="ClientId">Client the repository belongs to.</param>
/// <param name="ClientName">Display name, resolved by the caller.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="RepositoryName">Display name as the provider reported it, when a review recorded one.</param>
/// <param name="ReviewJobs">Completed review jobs in the window.</param>
/// <param name="JobsCollected">Of those, jobs the collection holds at least one finding for.</param>
/// <param name="ProducedFindings">
///     Findings those jobs persisted in their own results, counted without loading them: the ceiling an import
///     of this window could reach.
/// </param>
/// <param name="CollectedFindings">Findings the collection holds for those jobs.</param>
/// <param name="PullRequests">Distinct pull requests reviewed in the window.</param>
/// <param name="PullRequestsRetained">
///     Of those, pull requests whose threads are retained. Outcomes and misses can only be recovered where they
///     are, because a thread's resolution is not part of a review's own result.
/// </param>
/// <param name="RetainedThreads">Retained threads on those pull requests, whoever authored them.</param>
/// <param name="Dispositions">Outcomes the collection has recorded for its findings on them.</param>
/// <param name="Misses">Human threads harvested as findings the reviewer did not raise.</param>
/// <param name="PullRequestsSealed">Pull requests whose correctness has been sealed.</param>
public sealed record CodeInsightHistoryCoverageRow(
    Guid ClientId,
    string? ClientName,
    string RepositoryId,
    string? RepositoryName,
    int ReviewJobs,
    int JobsCollected,
    int ProducedFindings,
    int CollectedFindings,
    int PullRequests,
    int PullRequestsRetained,
    int RetainedThreads,
    int Dispositions,
    int Misses,
    int PullRequestsSealed);

/// <summary>
///     The coverage rows and the totals across them.
/// </summary>
/// <param name="Rows">One row per repository with review activity in the window, least covered first.</param>
/// <param name="ReviewJobs">Completed review jobs across every row.</param>
/// <param name="JobsCollected">Jobs the collection holds findings for.</param>
/// <param name="ProducedFindings">Findings persisted by those jobs.</param>
/// <param name="CollectedFindings">Findings the collection holds.</param>
/// <param name="PullRequests">Distinct pull requests reviewed.</param>
/// <param name="PullRequestsRetained">Pull requests whose threads are retained.</param>
/// <param name="ClientsWithCollectionOff">
///     Clients with review activity in the window that have collection switched off. An import cannot touch
///     them, and their absence from the numbers is a setting rather than a gap in the data.
/// </param>
public sealed record CodeInsightHistoryCoverage(
    IReadOnlyList<CodeInsightHistoryCoverageRow> Rows,
    int ReviewJobs,
    int JobsCollected,
    int ProducedFindings,
    int CollectedFindings,
    int PullRequests,
    int PullRequestsRetained,
    int ClientsWithCollectionOff)
{
    /// <summary>Nothing reviewed in the window, or nothing the caller may see.</summary>
    public static CodeInsightHistoryCoverage Empty { get; } = new([], 0, 0, 0, 0, 0, 0, 0);
}
