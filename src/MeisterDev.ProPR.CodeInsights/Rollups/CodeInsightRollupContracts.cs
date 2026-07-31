// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

/// <summary>
///     Which scope a roll-up groups by. Every value is a prefix of the projection's own key, which is why one
///     stored grain can serve all five: each is a <c>GROUP BY</c> over an index rather than a separate table.
/// </summary>
public enum CodeInsightGrain
{
    /// <summary>One row per client.</summary>
    Client = 0,

    /// <summary>One row per client and repository.</summary>
    Repository = 1,

    /// <summary>One row per client, repository, and pull request.</summary>
    PullRequest = 2,

    /// <summary>One row per client, repository, and file.</summary>
    File = 3,

    /// <summary>One row per review job.</summary>
    Job = 4,
}

/// <summary>How wide a time bucket a series uses. Week and month are derived from the stored day rows.</summary>
public enum CodeInsightBucketSize
{
    /// <summary>One point per day.</summary>
    Day = 0,

    /// <summary>One point per ISO week, anchored to its Monday.</summary>
    Week = 1,

    /// <summary>One point per calendar month, anchored to the first.</summary>
    Month = 2,
}

/// <summary>
///     What to include in a roll-up read. The authorised client set is supplied by the caller and is never
///     taken from a request: a cross-client aggregate over an unchecked set would be an exfiltration primitive.
/// </summary>
/// <param name="ClientIds">The clients the caller may see. An empty set yields an empty result, never everything.</param>
/// <param name="From">Inclusive start of the window, by review date.</param>
/// <param name="To">Inclusive end of the window, by review date.</param>
/// <param name="RepositoryId">Optional repository filter.</param>
/// <param name="PullRequestId">Optional pull-request filter.</param>
/// <param name="FilePath">Optional file filter.</param>
public sealed record CodeInsightRollupQuery(
    IReadOnlyCollection<Guid> ClientIds,
    DateOnly From,
    DateOnly To,
    string? RepositoryId = null,
    long? PullRequestId = null,
    string? FilePath = null);

/// <summary>One point of a counted series.</summary>
/// <param name="BucketStart">Start of the bucket: the day, the week's Monday, or the month's first.</param>
/// <param name="DimensionKey">The core type slug or disposition name, or the empty string for a plain total.</param>
/// <param name="Count">How many findings fell in this bucket for this dimension member.</param>
public sealed record CodeInsightSeriesPoint(DateOnly BucketStart, string DimensionKey, int Count);

/// <summary>One row of a concentration ranking.</summary>
/// <param name="ClientId">The client the scope belongs to.</param>
/// <param name="RepositoryId">Repository, when the grain includes one.</param>
/// <param name="PullRequestId">Pull request, when the grain includes one.</param>
/// <param name="FilePath">File, when the grain is per-file.</param>
/// <param name="JobId">Job, when the grain is per-job.</param>
/// <param name="Count">Findings attributed to this scope in the window.</param>
/// <param name="RepositoryName">
///     The repository's display name, when one has been recorded. <see langword="null" /> leaves the caller to show
///     <paramref name="RepositoryId" />, which is the provider's identifier and for several providers a bare number.
/// </param>
public sealed record CodeInsightConcentrationRow(
    Guid ClientId,
    string? RepositoryId,
    long? PullRequestId,
    string? FilePath,
    Guid? JobId,
    int Count,
    string? RepositoryName = null);

/// <summary>
///     One repository's own numbers, for the directory a reader lands on.
/// </summary>
/// <remarks>
///     The comparison this row supports is volume (where the findings are) and nothing finer. Two codebases'
///     averages are not comparable: they differ in size, language, age, and how much of them a review even looks
///     at. The directory exists so a reader picks a repository before reading anything derived.
/// </remarks>
/// <param name="ClientId">The client the repository belongs to.</param>
/// <param name="RepositoryId">Provider repository identifier: what every read filters on.</param>
/// <param name="RepositoryName">Display name, when one has been recorded.</param>
/// <param name="Findings">Findings collected in the window.</param>
/// <param name="PullRequests">Distinct pull requests that produced them.</param>
/// <param name="Files">Distinct files carrying them; pull-request-level findings are not one.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request, or <see langword="null" /> when none.</param>
/// <param name="LastActivityOn">The most recent day a finding was collected, so a stale repository looks stale.</param>
public sealed record CodeInsightRepositorySummary(
    Guid ClientId,
    string RepositoryId,
    string? RepositoryName,
    int Findings,
    int PullRequests,
    int Files,
    double? AveragePerPullRequest,
    DateOnly? LastActivityOn);

/// <summary>
///     The repository directory: every repository with findings in the window, busiest first, and the totals across
///     them.
/// </summary>
/// <param name="TotalFindings">Findings across every repository in scope.</param>
/// <param name="Repositories">How many repositories carried any.</param>
/// <param name="PullRequests">Distinct pull requests across all of them.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request across the whole scope.</param>
/// <param name="Rows">The repositories, most findings first.</param>
public sealed record CodeInsightRepositoryDirectory(
    int TotalFindings,
    int Repositories,
    int PullRequests,
    double? AveragePerPullRequest,
    IReadOnlyList<CodeInsightRepositorySummary> Rows);

/// <summary>
///     What a hotspot ranking groups by: the file a finding is in, or the definition inside it.
/// </summary>
public enum CodeInsightHotspotGrouping
{
    /// <summary>One row per file. Every collected finding with a path is counted.</summary>
    File = 0,

    /// <summary>
    ///     One row per definition, within its file. Only findings the file's syntax placed are counted, which is
    ///     fewer than the file grouping sees: the difference travels on the report rather than being hidden.
    /// </summary>
    Symbol = 1,
}

/// <summary>
///     One file's history: how much has been found in it, and across how many pull requests.
/// </summary>
/// <remarks>
///     The average is over the pull requests that raised at least one finding in the file: the only set the
///     collection can see. It is deliberately not "per pull request that touched the file": nothing here knows
///     which pull requests touched a file without finding anything in it, and quietly widening the denominator
///     would make every file look better the less it was reviewed.
/// </remarks>
/// <param name="FilePath">The file, or the empty string for findings raised about the pull request as a whole.</param>
/// <param name="Findings">Findings raised in this file across every pull request in scope.</param>
/// <param name="PullRequests">How many distinct pull requests raised at least one finding in it.</param>
/// <param name="AveragePerPullRequest">
///     Findings per such pull request, or <see langword="null" /> when there were none to divide by.
/// </param>
/// <param name="SymbolName">
///     The definition within the file, when the ranking is grouped by symbol. <see langword="null" /> for a
///     file-grouped row.
/// </param>
public sealed record CodeInsightFileHotspot(
    string FilePath,
    int Findings,
    int PullRequests,
    double? AveragePerPullRequest,
    string? SymbolName = null);

/// <summary>
///     The hotspot answer: which files keep producing findings, with the totals the per-file rows sit inside.
/// </summary>
/// <remarks>
///     The totals describe every file in scope, not only the rows returned, so a truncated list cannot make a
///     codebase look smaller than it is. <paramref name="FileCount" /> is what makes the truncation visible.
/// </remarks>
/// <param name="TotalFindings">Findings across every file in scope.</param>
/// <param name="PullRequests">Distinct pull requests that raised any of them.</param>
/// <param name="AveragePerPullRequest">Findings per such pull request across the whole scope.</param>
/// <param name="FileCount">How many distinct rows carried findings, before any truncation.</param>
/// <param name="Files">The worst rows, most findings first, truncated to what the caller asked for.</param>
/// <param name="UnplacedFindings">
///     Findings in scope this grouping cannot place (no resolved definition) and so counts nowhere above. Always
///     zero when grouping by file. Reported rather than folded into an "(unknown)" row, which would rank as if it
///     were somewhere in the code.
/// </param>
public sealed record CodeInsightHotspotReport(
    int TotalFindings,
    int PullRequests,
    double? AveragePerPullRequest,
    int FileCount,
    IReadOnlyList<CodeInsightFileHotspot> Files,
    int UnplacedFindings = 0);
