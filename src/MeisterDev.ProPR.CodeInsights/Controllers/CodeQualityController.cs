// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Survival;
using MeisterDev.ProPR.CodeInsights.Support;
using MeisterDev.ProPR.CodeInsights.Http;
using Microsoft.AspNetCore.Http;

namespace MeisterDev.ProPR.CodeInsights.Controllers;

/// <summary>
///     Serves the code-quality views: what kinds of problem a codebase keeps producing, where they cluster, and
///     the individual findings behind either.
/// </summary>
/// <remarks>
///     <para>
///         The audience is whoever writes the code, so client access plus the licence is the whole rule. These
///         reads deliberately expose no judgement of ProPR itself, no precision, no recall, no harvested misses.
///         Those live on the reviewer-performance surface, which is an operator's concern and gated as one.
///     </para>
///     <para>
///         An unlicensed installation is denied rather than emptied: this is a commercial area, and a deep link
///         must not succeed because a frontend flag was flipped. A licensed installation with no data in the
///         window returns an empty payload, because "nothing collected yet" is a state the view renders.
///     </para>
/// </remarks>
[ApiController]
[Route("code-quality")]
public sealed class CodeQualityController(
    CodeInsightScopeResolver scopeResolver,
    IClientAdminService clientAdminService,
    ICodeInsightRollupReader? rollupReader = null,
    ICodeInsightBrowseReader? browseReader = null,
    ICodeInsightSurvivalReader? survivalReader = null) : ControllerBase
{
    /// <summary>
    ///     Returns the counted type series: "what kinds of problem does this codebase keep producing".
    /// </summary>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="bucket">Bucket size: <c>day</c>, <c>week</c>, or <c>month</c>. Defaults to day.</param>
    /// <param name="clientId">Narrows to one client the caller may already see.</param>
    /// <param name="repositoryId">Narrows to one repository: the usual case for this audience.</param>
    /// <param name="pullRequestId">Narrows to one pull request, for the view embedded in a review.</param>
    /// <param name="filePath">Narrows to one file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The series, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no client access, lacks the licence, or asked for a client it cannot see.</response>
    [HttpGet("types-over-time")]
    [ProducesResponseType(typeof(CodeInsightTypeSeriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTypesOverTime(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? bucket = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] long? pullRequestId = null,
        [FromQuery] string? filePath = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForClientAccessAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (rollupReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightTypeSeriesResponse([], 0, []));
        }

        var query = CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId, pullRequestId, filePath);
        var points = await rollupReader.GetSeriesAsync(
            query,
            CodeInsightCountDimension.CoreType,
            CodeInsightQueries.ParseBucket(bucket),
            ct);
        var total = await rollupReader.GetTotalAsync(query, ct);

        return this.Ok(
            new CodeInsightTypeSeriesResponse(
                points
                    .Select(point => new CodeInsightCountPointResponse(point.BucketStart, point.DimensionKey, point.Count))
                    .ToList(),
                total,
                points
                    .Select(point => point.DimensionKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList()));
    }

    /// <summary>
    ///     Returns the top scopes by finding count at the requested grain: "where does it keep happening".
    /// </summary>
    /// <remarks>
    ///     Also how a caller finds the repository worth landing on: ranked at the repository grain, the first row is
    ///     the busiest repository the caller can see.
    /// </remarks>
    /// <param name="grain">Grain: <c>client</c>, <c>repository</c>, <c>pullRequest</c>, <c>file</c>, or <c>job</c>.</param>
    /// <param name="topN">How many rows to return. Clamped to 1–100.</param>
    /// <param name="from">Inclusive start of the window. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller may already see.</param>
    /// <param name="repositoryId">Narrows to one repository, for drilling from a repository into its files.</param>
    /// <param name="pullRequestId">Narrows to one pull request, for the view embedded in a review.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The ranking, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no client access, lacks the licence, or asked for a client it cannot see.</response>
    [HttpGet("concentration")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeInsightConcentrationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetConcentration(
        [FromQuery] string? grain = null,
        [FromQuery] int topN = 10,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] long? pullRequestId = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForClientAccessAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (rollupReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(Array.Empty<CodeInsightConcentrationResponse>());
        }

        var rows = await rollupReader.GetConcentrationAsync(
            CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId, pullRequestId),
            CodeInsightQueries.ParseGrain(grain),
            Math.Clamp(topN, 1, 100),
            ct);

        var names = await this.ResolveClientNamesAsync(rows.Select(row => row.ClientId).Distinct().ToList(), ct);

        return this.Ok(
            rows
                .Select(row => new CodeInsightConcentrationResponse(
                    row.ClientId,
                    names.GetValueOrDefault(row.ClientId),
                    row.RepositoryId,
                    row.PullRequestId,
                    row.FilePath,
                    row.Count,
                    row.RepositoryName))
                .ToList());
    }

    /// <summary>
    ///     Returns the repository directory, every repository with findings in the window, busiest first, with the
    ///     totals across them.
    /// </summary>
    /// <remarks>
    ///     What a reader picks from. Everything else on this surface describes one codebase, and codebases are not
    ///     comparable to each other on anything but volume: they differ in size, language, age, and how much of them
    ///     a review looks at. So this read deliberately ignores a repository narrowing: it is the list of
    ///     alternatives, and narrowing it to the current choice would hide them.
    /// </remarks>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller may already see.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The directory, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no client access, lacks the licence, or asked for a client it cannot see.</response>
    [HttpGet("repositories")]
    [ProducesResponseType(typeof(CodeInsightRepositoryDirectoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRepositories(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForClientAccessAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (rollupReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightRepositoryDirectoryResponse(0, 0, 0, null, []));
        }

        var directory = await rollupReader.GetRepositoryDirectoryAsync(
            CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to),
            ct);

        var names = await this.ResolveClientNamesAsync(
            directory.Rows.Select(row => row.ClientId).Distinct().ToList(),
            ct);

        return this.Ok(
            new CodeInsightRepositoryDirectoryResponse(
                directory.TotalFindings,
                directory.Repositories,
                directory.PullRequests,
                directory.AveragePerPullRequest,
                directory.Rows
                    .Select(row => new CodeInsightRepositorySummaryResponse(
                        row.ClientId,
                        names.GetValueOrDefault(row.ClientId),
                        row.RepositoryId,
                        row.RepositoryName,
                        row.Findings,
                        row.PullRequests,
                        row.Files,
                        row.AveragePerPullRequest,
                        row.LastActivityOn))
                    .ToList()));
    }

    /// <summary>
    ///     Returns the file hotspots: "which files keep producing findings, and how many per pull request".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         History by definition, so a pull-request filter is not honoured here. What a pull request can do is
    ///         choose the files: <paramref name="filesFromPullRequestId" /> restricts the ranking to the files that
    ///         pull request raised findings in, while every count still spans every pull request in scope. That is
    ///         what lets the view embedded in a review say "this file has produced thirty findings before today".
    ///     </para>
    ///     <para>
    ///         The averages are over the pull requests that raised at least one finding in a file. That is the only
    ///         denominator the collection can see, and it is narrower than "pull requests that touched the file",
    ///         a caller must not present it as the latter.
    ///     </para>
    /// </remarks>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller may already see.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="filesFromPullRequestId">Restricts the files considered to those one pull request found something in.</param>
    /// <param name="groupBy">
    ///     <c>file</c> (default) or <c>symbol</c>. Grouped by symbol the rows are definitions within their files, and
    ///     only findings the file's syntax placed are counted: the remainder comes back as <c>unplacedFindings</c>.
    /// </param>
    /// <param name="topN">How many rows to return. Clamped to 1–200.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The hotspots, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no client access, lacks the licence, or asked for a client it cannot see.</response>
    [HttpGet("hotspots")]
    [ProducesResponseType(typeof(CodeInsightHotspotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetHotspots(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] long? filesFromPullRequestId = null,
        [FromQuery] string? groupBy = null,
        [FromQuery] int topN = 25,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForClientAccessAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (rollupReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightHotspotResponse(0, 0, null, 0, []));
        }

        var report = await rollupReader.GetHotspotsAsync(
            CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId),
            filesFromPullRequestId,
            Math.Clamp(topN, 1, 200),
            CodeInsightQueries.ParseHotspotGrouping(groupBy),
            ct);

        return this.Ok(
            new CodeInsightHotspotResponse(
                report.TotalFindings,
                report.PullRequests,
                report.AveragePerPullRequest,
                report.FileCount,
                report.Files
                    .Select(file => new CodeInsightFileHotspotResponse(
                        file.FilePath,
                        file.Findings,
                        file.PullRequests,
                        file.AveragePerPullRequest,
                        file.SymbolName))
                    .ToList(),
                report.UnplacedFindings));
    }

    /// <summary>
    ///     Returns how much of what was raised stuck: "of the problems reviews found here, how many were still
    ///     being reported when the pull request finished".
    /// </summary>
    /// <remarks>
    ///     Pull requests reviewed only once are excluded, because every problem in them is trivially still present
    ///     at the newest increment; including them would report near-perfect persistence for work that was never
    ///     given the chance to shed anything.
    /// </remarks>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller may already see.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="pullRequestId">
    ///     Narrows to one pull request, for the view embedded in a review. A pull request reviewed only once still
    ///     reports nothing, for the reason above.
    /// </param>
    /// <param name="topN">How many pull requests to break out. Clamped to 1–50.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The survival counts, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no client access, lacks the licence, or asked for a client it cannot see.</response>
    [HttpGet("survival")]
    [ProducesResponseType(typeof(CodeInsightSurvivalReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSurvival(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] long? pullRequestId = null,
        [FromQuery] int topN = 10,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForClientAccessAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (survivalReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightSurvivalReport(ToSurvival(default), []));
        }

        var query = CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId, pullRequestId);

        var total = await survivalReader.GetSurvivalAsync(query, ct);
        var perPullRequest = await survivalReader.GetSurvivalByPullRequestAsync(query, Math.Clamp(topN, 1, 50), ct);

        return this.Ok(
            new CodeInsightSurvivalReport(
                ToSurvival(total),
                perPullRequest
                    .Select(row => new CodeInsightPullRequestSurvivalResponse(
                        row.ClientId,
                        row.RepositoryId,
                        row.PullRequestId,
                        row.Revisions,
                        ToSurvival(row.Counts),
                        row.RepositoryName))
                    .ToList()));
    }

    /// <summary>
    ///     Returns the findings behind a number, so anything on a view can be opened up and checked.
    /// </summary>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller may already see.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="pullRequestId">Narrows to one pull request.</param>
    /// <param name="filePath">Narrows to one file.</param>
    /// <param name="coreType">Narrows to one core type slug: what a click on a type series means.</param>
    /// <param name="disposition">Narrows to one outcome.</param>
    /// <param name="limit">Maximum rows. Clamped to 1–200.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The findings, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no client access, lacks the licence, or asked for a client it cannot see.</response>
    [HttpGet("findings")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeInsightFindingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFindings(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] long? pullRequestId = null,
        [FromQuery] string? filePath = null,
        [FromQuery] string? coreType = null,
        [FromQuery] string? symbolName = null,
        [FromQuery] string? disposition = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForClientAccessAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (browseReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(Array.Empty<CodeInsightFindingResponse>());
        }

        var rows = await browseReader.ListFindingsAsync(
            CodeInsightQueries.BuildBrowseQuery(
                scope.ClientIds,
                from,
                to,
                repositoryId,
                pullRequestId,
                filePath,
                coreType,
                CodeInsightQueries.ParseDisposition(disposition),
                limit,
                symbolName),
            ct);

        return this.Ok(
            rows
                .Select(row => new CodeInsightFindingResponse(
                    row.Id,
                    row.ClientId,
                    row.RepositoryId,
                    row.PullRequestId,
                    row.JobId,
                    row.FilePath,
                    row.LineNumber,
                    row.Severity.ToString(),
                    row.Message,
                    row.CoreTags,
                    row.Disposition?.ToString(),
                    row.ProviderThreadId,
                    row.ObservedAt))
                .ToList());
    }

    private static CodeInsightSurvivalResponse ToSurvival(CodeInsightSurvivalCounts counts)
    {
        return new CodeInsightSurvivalResponse(
            counts.Persisted,
            counts.Fixed,
            counts.Dropped,
            counts.Total,
            counts.PersistenceRate,
            counts.PullRequests);
    }

    private async Task<Dictionary<Guid, string>> ResolveClientNamesAsync(
        IReadOnlyList<Guid> clientIds,
        CancellationToken ct)
    {
        if (clientIds.Count == 0)
        {
            return [];
        }

        // A ranking of opaque identifiers is not a ranking anybody can act on. A lookup failure is not worth
        // failing the read over, though: the row still carries its identifier.
        try
        {
            var clients = await clientAdminService.GetByIdsAsync(clientIds, ct);
            return clients.ToDictionary(client => client.Id, client => client.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }
}
