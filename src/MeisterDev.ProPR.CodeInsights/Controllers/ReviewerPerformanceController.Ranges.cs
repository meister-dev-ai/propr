// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.CodeInsights.Controllers;

public sealed partial class ReviewerPerformanceController
{
    /// <summary>Returns versioned interpretation ranges from retained counts for one or two consistently read views.</summary>
    /// <param name="query">Bounded dates, dimensions, cumulative or period mode, and optional measurement matrix.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Counts, coherent scenario tuples, summaries, evidence limits and authorized facets.</response>
    /// <response code="400">Unsupported query or exceeded bounds.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Missing tenant administration, license or selected-client access.</response>
    /// <response code="503">Database analytics services are unavailable.</response>
    [HttpPost("ranges/query")]
    [ProducesResponseType(typeof(ReviewerPerformanceRangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> QueryRanges([FromBody] ReviewerPerformanceQuery query, CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (rangeReader is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return this.Ok(await rangeReader.QueryAsync(query, scope.ClientIds, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return this.Forbid();
        }
        catch (ArgumentException exception)
        {
            return this.BadRequest(
                new ProblemDetails
                {
                    Title = "Invalid performance query",
                    Detail = exception.Message,
                    Status = 400
                });
        }
    }

    /// <summary>Captures and saves a complete server response under one consistent database snapshot.</summary>
    /// <param name="request">Retry-safe report identifier, name and authorized query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Stored capture, including on an identical retry.</response>
    /// <response code="400">Invalid name, query or payload size.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Missing administration, license or access to every included client.</response>
    /// <response code="409">The report identifier belongs to a different request.</response>
    /// <response code="503">Database analytics services are unavailable.</response>
    [HttpPost("reports")]
    [ProducesResponseType(typeof(ReviewerPerformanceSavedReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SaveReport([FromBody] ReviewerPerformanceSaveReportRequest request, CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (reportStore is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return this.Ok(await reportStore.SaveAsync(request, scope.ClientIds, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return this.Forbid();
        }
        catch (ArgumentException exception)
        {
            return this.BadRequest(
                new ProblemDetails
                {
                    Title = "Invalid report request",
                    Detail = exception.Message,
                    Status = 400
                });
        }
        catch (InvalidOperationException exception)
        {
            return this.Conflict(
                new ProblemDetails
                {
                    Title = "Report identifier conflict",
                    Detail = exception.Message,
                    Status = 409
                });
        }
    }

    /// <summary>Lists up to 100 unexpired reports for which every contained client is authorized.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Authorized report metadata, newest first.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Missing tenant administration or license.</response>
    [HttpGet("reports")]
    [ProducesResponseType(typeof(IReadOnlyList<ReviewerPerformanceReportSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListReports(CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        return this.Ok(reportStore is null ? Array.Empty<ReviewerPerformanceReportSummary>() : await reportStore.ListAsync(scope.ClientIds, ct));
    }

    /// <summary>Opens the stored response without evaluating current evidence or recalculating its version.</summary>
    /// <param name="id">Saved report identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Immutable saved counts, results, filters and evidence metadata.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Missing tenant administration or license.</response>
    /// <response code="404">Report absent, expired or containing a client outside the authorized scope.</response>
    [HttpGet("reports/{id:guid}")]
    [ProducesResponseType(typeof(ReviewerPerformanceSavedReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OpenReport(Guid id, CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        var report = reportStore is null ? null : await reportStore.GetAsync(id, scope.ClientIds, ct);
        return report is null ? this.NotFound() : this.Ok(report);
    }

    /// <summary>Deletes a complete saved report when all contained clients are authorized.</summary>
    /// <param name="id">Saved report identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Complete report removed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Missing tenant administration or license.</response>
    /// <response code="404">Report absent, expired or containing an unauthorized client.</response>
    [HttpDelete("reports/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteReport(Guid id, CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        return reportStore is not null && await reportStore.DeleteAsync(id, scope.ClientIds, ct) ? this.NoContent() : this.NotFound();
    }
}
