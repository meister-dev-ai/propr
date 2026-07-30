// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Controllers;

/// <summary>
///     Serves the collected classification of a review job's findings, so a review view can show what kind of
///     problem each finding describes alongside the finding itself.
/// </summary>
/// <remarks>
///     Kept off the review-result endpoint on purpose. The review contract is what the review path produces and
///     stays unaware of this slice; a caller that wants the tags asks for them separately and lines them up by
///     ordinal. That also means the review view degrades to exactly its previous behaviour when the slice is
///     absent, unlicensed, or the client never opted in.
/// </remarks>
[ApiController]
[Route("reviewing/jobs")]
public sealed class JobCodeInsightsController(
    IJobRepository jobRepository,
    ICodeInsightClassificationStore? classificationStore = null,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    /// <summary>
    ///     Returns the classification of each finding of the job, keyed by the finding's position in the job's
    ///     review result. Empty when nothing was collected for the job, when Code Insights is not licensed, or
    ///     when the slice is not registered: an empty list rather than an error, because "no tags" is a normal
    ///     state a review view has to render anyway.
    /// </summary>
    /// <param name="id">Review job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Classifications returned, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the job's client.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("{id:guid}/code-insights/findings")]
    [HttpGet("/jobs/{id:guid}/code-insights/findings")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeInsightFindingClassificationView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFindingClassifications(Guid id, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var job = jobRepository.GetById(id);
        if (job is null)
        {
            return this.NotFound();
        }

        // Same scoping as the review result this sits beside: whoever may read the findings may read their tags.
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, job.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (classificationStore is null || !await this.IsLicensedAsync(ct))
        {
            // Reading is gated as well as collecting. An installation that collected while Commercial and has
            // since downgraded still holds the rows, and they must stop being served.
            return this.Ok(Array.Empty<CodeInsightFindingClassificationView>());
        }

        var classifications = await classificationStore.GetClassificationsForJobAsync(
            id,
            CodeInsightClassificationSweeper.DefaultMaxAttempts,
            ct);

        return this.Ok(classifications);
    }

    private async Task<bool> IsLicensedAsync(CancellationToken ct)
    {
        // Fails closed, like the collection gate: an edition that cannot be established serves nothing.
        if (licensingCapabilityService is null)
        {
            return false;
        }

        try
        {
            return await licensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
