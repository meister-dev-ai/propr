// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Global admin endpoints for installation-wide provider-family activation policy.</summary>
[ApiController]
public sealed class AdminProvidersController(IProviderActivationService? providerActivationService = null)
    : ControllerBase
{
    /// <summary>Returns the activation status for every supported provider family.</summary>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">Returns the activation status for all supported provider families.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="503">Provider activation services are unavailable.</response>
    [HttpGet("/admin/providers")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderActivationStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListProviders(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (providerActivationService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await providerActivationService.ListAsync(ct));
    }

    /// <summary>Updates whether one provider family is enabled for this installation.</summary>
    /// <param name="provider">The provider family to update.</param>
    /// <param name="request">The requested activation state.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">Returns the updated activation state for the provider family.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a global administrator.</response>
    /// <response code="503">Provider activation services are unavailable.</response>
    [HttpPatch("/admin/providers/{provider}")]
    [ProducesResponseType(typeof(ProviderActivationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PatchProvider(
        ScmProvider provider,
        [FromBody] PatchAdminProviderRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (providerActivationService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await providerActivationService.SetEnabledAsync(provider, request.IsEnabled, ct));
    }
}

/// <summary>Request body for updating one provider family's activation state.</summary>
/// <param name="IsEnabled">Whether the provider family should be enabled installation-wide.</param>
public sealed record PatchAdminProviderRequest(bool IsEnabled);
