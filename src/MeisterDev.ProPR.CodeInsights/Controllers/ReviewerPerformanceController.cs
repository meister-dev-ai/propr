// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.History;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Support;
using MeisterDev.ProPR.CodeInsights.Http;
using Microsoft.AspNetCore.Http;

namespace MeisterDev.ProPR.CodeInsights.Controllers;

/// <summary>
///     Serves the reviewer-performance views: whether ProPR is right and improving, whether humans want what it
///     says, and what it failed to raise.
/// </summary>
/// <remarks>
///     <para>
///         An operator surface, gated on tenant administration rather than client access. Two reasons, and the
///         second matters more. These numbers judge the tool, which is a purchasing and configuration question
///         rather than a development one. And the evidence underneath them (every disposition, every harvested
///         miss) is <em>AI-judged and not yet calibrated</em>; put in front of a whole engineering organisation it
///         would be read as fact about individual reviews rather than as an estimate about the reviewer.
///     </para>
///     <para>
///         The code-quality surface carries what a developer needs from the same collected findings, at client
///         access, and deliberately exposes none of this.
///     </para>
/// </remarks>
[ApiController]
[Route("reviewer-performance")]
public sealed class ReviewerPerformanceController(
    IOptionsMonitor<CodeInsightsOptions> options,
    CodeInsightScopeResolver scopeResolver,
    IClientAdminService clientAdminService,
    ICodeInsightMetricReader? metricReader = null,
    ICodeInsightBrowseReader? browseReader = null,
    ICodeInsightHistoryReader? historyReader = null,
    ICodeInsightHistoryImporter? historyImporter = null) : ControllerBase
{
    /// <summary>
    ///     Sealed pull requests a correctness metric needs before a caller should present it as precise.
    ///     Provisional: the number a classifier evaluation will calibrate. Configurable so an installation can
    ///     move it without a release, via <c>CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS</c>.
    /// </summary>
    public const int DefaultMinimumSampleSize = 10;

    /// <summary>
    ///     Returns both metric lenses over the window ("is the reviewer right and improving" and "do humans want
    ///     what it says") as series, as totals, and with the direction each moved.
    /// </summary>
    /// <param name="from">Inclusive start of the window. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window. Defaults to today.</param>
    /// <param name="bucket">Bucket size: <c>day</c>, <c>week</c>, or <c>month</c>. Defaults to week.</param>
    /// <param name="clientId">Narrows to one client the caller administers.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Both lenses, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not administer a tenant, lacks the licence, or named a client it cannot see.</response>
    [HttpGet("quality")]
    [ProducesResponseType(typeof(CodeInsightQualityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetQuality(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? bucket = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        var minimumSampleSize = this.ResolveMinimumSampleSize();

        if (metricReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(EmptyQuality(minimumSampleSize));
        }

        var query = CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId);
        var bucketSize = CodeInsightQueries.ParseBucket(bucket, CodeInsightBucketSize.Week);

        var correctness = await metricReader.GetCorrectnessSeriesAsync(query, bucketSize, ct);
        var acceptance = await metricReader.GetAcceptanceSeriesAsync(query, bucketSize, ct);
        var correctnessTotal = await metricReader.GetCorrectnessAsync(query, ct);
        var acceptanceTotal = await metricReader.GetAcceptanceAsync(query, ct);

        return this.Ok(
            new CodeInsightQualityResponse(
                correctness.Select(CodeInsightQueries.ToPoint).ToList(),
                acceptance.Select(CodeInsightQueries.ToPoint).ToList(),
                CodeInsightQueries.ToMetric(correctnessTotal),
                CodeInsightQueries.ToMetric(acceptanceTotal),
                ResolveTrend(correctness, point => point.Result.Metrics.F1, minimumSampleSize),
                // Acceptance rests on resolved findings rather than closed pull requests, so the sealed-pull-request
                // floor would be the wrong bar for it. Its own sample is the count it is a proportion of.
                ResolveTrend(acceptance, point => point.Result.Metrics.AcceptanceRate, minimumSample: 1),
                minimumSampleSize,
                CodeInsightTrendAnalyzer.MinimumPeriods));
    }

    /// <summary>
    ///     Returns correctness grouped by scope: whether the reviewer is working everywhere, or one client,
    ///     repository, or pull request is carrying the whole shortfall.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the grains a seal has. A measurement is sealed per pull request, so client, repository, and pull
    ///         request are meaningful and finer grains are not; asking for one is answered at the pull-request grain
    ///         rather than with an empty list. Every row is computed from its own summed counts, never by averaging
    ///         the rows beneath it, which would weight a pull request with one finding like one with a hundred.
    ///     </para>
    ///     <para>
    ///         <c>model</c> groups by what produced the findings instead of by where they landed: the reading that
    ///         answers whether a cheaper model would have done. It comes from the findings rather than the seals,
    ///         because one pull request can be reviewed by several models, and it reports only the ratios a model can
    ///         be held to: precision and acceptance, never recall or F1.
    ///     </para>
    /// </remarks>
    /// <param name="grain">
    ///     Grain: <c>client</c>, <c>repository</c>, <c>pullRequest</c>, or <c>model</c>. Defaults to repository.
    /// </param>
    /// <param name="from">Inclusive start of the window, by seal date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by seal date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller administers.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The grouped metrics, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not administer a tenant, lacks the licence, or named a client it cannot see.</response>
    [HttpGet("by-grain")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeInsightScopedMetricResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetByGrain(
        [FromQuery] string? grain = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (metricReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(Array.Empty<CodeInsightScopedMetricResponse>());
        }

        var query = CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId);

        if (CodeInsightQueries.IsModelGrain(grain))
        {
            var models = await metricReader.GetByModelAsync(query, ct);

            return this.Ok(
                models
                    .Select(row => new CodeInsightScopedMetricResponse(
                        // A model row is not a client scope: it spans every client the caller administers, and
                        // naming one of them would be a claim the number does not make.
                        ClientId: null,
                        ClientName: null,
                        RepositoryId: null,
                        PullRequestId: null,
                        CodeInsightQueries.ToMetric(row.Result),
                        row.ModelId,
                        row.LogicalModelName))
                    .ToList());
        }

        var rows = await metricReader.GetCorrectnessByGrainAsync(
            query,
            CodeInsightQueries.ParseGrain(grain),
            ct);

        var names = await this.ResolveClientNamesAsync(rows.Select(row => row.ClientId).Distinct().ToList(), ct);

        return this.Ok(
            rows
                .Select(row => new CodeInsightScopedMetricResponse(
                    row.ClientId,
                    names.GetValueOrDefault(row.ClientId),
                    row.RepositoryId,
                    row.PullRequestId,
                    CodeInsightQueries.ToMetric(row.Result),
                    RepositoryName: row.RepositoryName))
                // Worst first: a ranked list of where the reviewer is weakest is the reason to group at all. A row
                // with no computable correctness sorts last rather than as a zero it never earned.
                .OrderBy(row => row.Metric.F1 ?? double.MaxValue)
                .ThenByDescending(row => row.Metric.SampleSize)
                .ToList());
    }

    /// <summary>
    ///     Returns the harvested human threads (what the reviewer did not raise) with all three judgements,
    ///     including the threads that did not qualify.
    /// </summary>
    /// <remarks>
    ///     The non-qualifying rows are the point, not noise: recall depends on where the "should have caught this"
    ///     line sits, and nobody can calibrate that line without seeing what it currently excludes. They are also
    ///     the most easily misread data in the product, which is why this read sits behind administration.
    /// </remarks>
    /// <param name="from">Inclusive start of the window, by harvest date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by harvest date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller administers.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="pullRequestId">Narrows to one pull request.</param>
    /// <param name="limit">Maximum rows. Clamped to 1–200.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The harvested threads, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not administer a tenant, lacks the licence, or named a client it cannot see.</response>
    [HttpGet("misses")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeInsightMissResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMisses(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] long? pullRequestId = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (browseReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(Array.Empty<CodeInsightMissResponse>());
        }

        var rows = await browseReader.ListMissesAsync(
            CodeInsightQueries.BuildBrowseQuery(
                scope.ClientIds,
                from,
                to,
                repositoryId,
                pullRequestId,
                null,
                null,
                null,
                limit),
            ct);

        return this.Ok(
            rows
                .Select(row => new CodeInsightMissResponse(
                    row.Id,
                    row.ClientId,
                    row.RepositoryId,
                    row.PullRequestId,
                    row.ProviderThreadId,
                    row.FilePath,
                    row.LineNumber,
                    row.Discussion,
                    row.IsSubstantive,
                    row.WasActedOn,
                    row.IsInScope,
                    row.CountsAsMiss,
                    row.ClassifierConfidence,
                    row.HarvestedAt))
                .ToList());
    }

    /// <summary>
    ///     Returns the findings behind a reviewer-performance number: the false positives behind a precision
    ///     figure, the fixes behind an acceptance rate.
    /// </summary>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller administers.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="disposition">Narrows to one outcome: what a click on an outcome means.</param>
    /// <param name="limit">Maximum rows. Clamped to 1–200.</param>
    /// <param name="rejectionReason">
    ///     Narrows to one rejection reason: what a click on a reason in the distribution means. A reason already
    ///     implies its outcome, so it needs no <paramref name="disposition" /> beside it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The findings, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not administer a tenant, lacks the licence, or named a client it cannot see.</response>
    [HttpGet("findings")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeInsightFindingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFindings(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] string? disposition = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? rejectionReason = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, clientId, ct);
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
                null,
                null,
                null,
                CodeInsightQueries.ParseDisposition(disposition),
                limit,
                symbolName: null,
                CodeInsightQueries.ParseRejectionReason(rejectionReason)),
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
                    row.ObservedAt,
                    row.RejectionReason?.ToString()))
                .ToList());
    }

    /// <summary>
    ///     Returns why the rejections in the window were rejected.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A precision number says how often the reviewer was turned down, and nothing about what to change.
    ///         These five reasons each point somewhere different: a reviewer that invents problems needs a better
    ///         prompt, one that argues with deliberate decisions needs the codebase's conventions, one that
    ///         repeats another tool needs to be told what that tool already covers.
    ///     </para>
    ///     <para>
    ///         Rejections carrying no reason are reported as their own count rather than folded into one. The
    ///         reason could not be judged, or the outcome was decided before reasons were recorded, and neither
    ///         of those is a reason.
    ///     </para>
    /// </remarks>
    /// <param name="from">Inclusive start of the window, by review date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window, by review date. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller administers.</param>
    /// <param name="repositoryId">Narrows to one repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The distribution, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not administer a tenant, lacks the licence, or named a client it cannot see.</response>
    [HttpGet("rejection-reasons")]
    [ProducesResponseType(typeof(CodeInsightRejectionReasonsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRejectionReasons(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? repositoryId = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (metricReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightRejectionReasonsResponse([], 0, 0, []));
        }

        var breakdown = await metricReader.GetRejectionReasonsAsync(
            CodeInsightQueries.BuildRollupQuery(scope.ClientIds, from, to, repositoryId),
            ct);

        return this.Ok(
            new CodeInsightRejectionReasonsResponse(
                Ranked(breakdown.Counts),
                breakdown.Unclassified,
                breakdown.Rejections,
                breakdown.ByConcernClass
                    .Select(row => new CodeInsightConcernClassRejectionsResponse(
                        row.ConcernClass?.ToString(),
                        Ranked(row.Counts),
                        row.WithoutReason,
                        row.Rejections))
                    .ToList()));
    }

    /// <summary>Largest first, because the reason worth acting on is the one that happens most.</summary>
    /// <summary>
    ///     Replays reviews that ran before collection was switched on into it, for one client and one window.
    /// </summary>
    /// <remarks>
    ///     Bounded and repeatable. Findings, roll-ups and coverage cost nothing; asking for outcomes replays what
    ///     became of each finding and the human threads it missed, which is the only part that calls a model.
    /// </remarks>
    /// <param name="body">The client, the window, and whether to include outcomes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">What the run read and wrote.</response>
    /// <response code="400">The window is inverted, or no client was named.</response>
    /// <response code="403">The caller does not administer the client's tenant, or the licence is absent.</response>
    [HttpPost("import")]
    [ProducesResponseType<CodeInsightImportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Import(
        [FromBody] CodeInsightImportRequestBody body,
        CancellationToken ct = default)
    {
        if (body is null || body.ClientId == Guid.Empty)
        {
            return this.BadRequest(new { error = "A client must be named." });
        }

        if (body.To < body.From)
        {
            return this.BadRequest(new { error = "The window ends before it starts." });
        }

        // The same resolver every read on this surface uses, so the licence and the tenancy rule are decided in one
        // place. Naming a client outside the caller's tenants is a denial rather than an empty run.
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, body.ClientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (historyImporter is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightImportResponse(0, 0, 0, 0, 0, 0, 0, 0, true, false, 0, 0));
        }

        var result = await historyImporter.ImportAsync(
            new CodeInsightImportRequest(
                body.ClientId,
                body.From,
                body.To,
                body.IncludeOutcomes,
                body.MaxJobs ?? CodeInsightImportRequest.DefaultMaxJobs),
            ct);

        return this.Ok(
            new CodeInsightImportResponse(
                result.JobsRead,
                result.JobsImported,
                result.JobsAlreadyCollected,
                result.FindingsImported,
                result.FindingsWithoutThread,
                result.PullRequests,
                result.OutcomeThreadsReplayed,
                result.HumanThreadsReplayed,
                result.CollectionDisabled,
                result.ReachedLimit,
                result.FindingsAlreadyHeld,
                result.ThreadsNotReplayable));
    }

    private static List<CodeInsightRejectionReasonCountResponse> Ranked(IReadOnlyDictionary<CodeInsightRejectionReason, int> counts)
    {
        return counts
            .OrderByDescending(entry => entry.Value)
            // A stable tie-break, so two reasons with equal counts do not swap places between reads.
            .ThenBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
            .Select(entry => new CodeInsightRejectionReasonCountResponse(entry.Key.ToString(), entry.Value))
            .ToList();
    }

    private static CodeInsightQualityResponse EmptyQuality(int minimumSampleSize)
    {
        var empty = CodeInsightQueries.ToMetric(new CodeInsightMetricResult(CodeInsightMetricCalculator.Compute(default), 0));

        var noTrend = new CodeInsightTrendResponse(CodeInsightTrendDirection.Insufficient, null, null, null, 0);

        return new CodeInsightQualityResponse(
            [],
            [],
            empty,
            empty,
            noTrend,
            noTrend,
            minimumSampleSize,
            CodeInsightTrendAnalyzer.MinimumPeriods);
    }

    /// <summary>
    ///     Tests the buckets that carry enough sample for a trend, rather than comparing the first against the last.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Buckets below the sample floor are skipped rather than counted as zero, because a ratio from two
    ///         closed pull requests is not evidence and a zero drawn in its place is worse than a gap.
    ///     </para>
    ///     <para>
    ///         Rising and falling are mapped to improving and declining because both metrics here are ones where
    ///         higher is better. A metric where a rise is bad news would need its own mapping rather than this one.
    ///     </para>
    /// </remarks>
    private static CodeInsightTrendResponse ResolveTrend(
        IReadOnlyList<CodeInsightMetricSeriesPoint> series,
        Func<CodeInsightMetricSeriesPoint, double?> select,
        int minimumSample)
    {
        var values = series
            .Where(point => point.Result.SampleSize >= minimumSample && select(point) is not null)
            .OrderBy(point => point.BucketStart)
            .Select(point => select(point)!.Value)
            .ToList();

        var trend = CodeInsightTrendAnalyzer.Analyse(values);

        var direction = trend.Verdict switch
        {
            CodeInsightTrendVerdict.Rising => CodeInsightTrendDirection.Improving,
            CodeInsightTrendVerdict.Falling => CodeInsightTrendDirection.Declining,
            CodeInsightTrendVerdict.Flat => CodeInsightTrendDirection.Flat,
            _ => CodeInsightTrendDirection.Insufficient,
        };

        return new CodeInsightTrendResponse(
            direction,
            trend.Tau,
            trend.PValue,
            trend.SlopePerPeriod,
            trend.Periods);
    }

    /// <summary>
    ///     Returns how much of the review history that already exists the collection knows about, per repository.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Collection starts the day the licence and the per-client toggle are both on, and nothing imports what
    ///         ran before that. Every other number on this surface is therefore silent about earlier reviews, and
    ///         silence reads exactly like a reviewer that found nothing. This read is the difference: per repository,
    ///         the findings the reviews themselves persisted against the findings the collection holds, the pull
    ///         requests whose threads are retained (the only ones an outcome can still be recovered from), and how
    ///         many pull requests have been sealed.
    ///     </para>
    ///     <para>
    ///         It counts rows that already exist. No provider call, no model token, nothing written.
    ///     </para>
    /// </remarks>
    /// <param name="from">Inclusive start of the window, by review submission date. Defaults to 30 days ago.</param>
    /// <param name="to">Inclusive end of the window. Defaults to today.</param>
    /// <param name="clientId">Narrows to one client the caller administers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Coverage per repository, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not administer a tenant, lacks the licence, or named a client it cannot see.</response>
    [HttpGet("coverage")]
    [ProducesResponseType(typeof(CodeInsightCoverageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCoverage(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, clientId, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (historyReader is null || scope.ClientIds.Count == 0)
        {
            return this.Ok(new CodeInsightCoverageResponse(0, 0, 0, 0, 0, 0, 0, []));
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var coverage = await historyReader.GetCoverageAsync(
            new CodeInsightHistoryCoverageQuery(
                scope.ClientIds,
                from ?? today.AddDays(-30),
                to ?? today),
            ct);

        var names = await this.ResolveClientNamesAsync(
            coverage.Rows.Select(row => row.ClientId).Distinct().ToList(),
            ct);

        return this.Ok(
            new CodeInsightCoverageResponse(
                coverage.ReviewJobs,
                coverage.JobsCollected,
                coverage.ProducedFindings,
                coverage.CollectedFindings,
                coverage.PullRequests,
                coverage.PullRequestsRetained,
                coverage.ClientsWithCollectionOff,
                coverage.Rows
                    .Select(row => new CodeInsightCoverageRowResponse(
                        row.ClientId,
                        names.GetValueOrDefault(row.ClientId),
                        row.RepositoryId,
                        row.RepositoryName,
                        row.ReviewJobs,
                        row.JobsCollected,
                        row.ProducedFindings,
                        row.CollectedFindings,
                        row.PullRequests,
                        row.PullRequestsRetained,
                        row.RetainedThreads,
                        row.Dispositions,
                        row.Misses,
                        row.PullRequestsSealed))
                    .ToList()));
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

    /// <summary>
    ///     The sample floor this surface applies, read per request so a recalibration takes effect without a
    ///     restart. Its own floor of one lives on the options type: a threshold of zero would mean presenting a
    ///     metric computed from nothing as precise, which is the failure the threshold exists to prevent.
    /// </summary>
    private int ResolveMinimumSampleSize()
    {
        return options.CurrentValue.EffectiveMinimumSealedPullRequests;
    }
}
