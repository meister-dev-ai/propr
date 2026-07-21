// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Licensing.Controllers;

/// <summary>Public bootstrap endpoints for login-time licensing awareness.</summary>
// Serves sign-in options before the user has any credentials, so it must be reachable anonymously.
[AllowAnonymous]
[ApiController]
[Route("auth/options")]
public sealed class AuthOptionsController(
    IConfiguration configuration,
    ILicensingCapabilityService? licensingCapabilityService = null)
    : ControllerBase
{
    /// <summary>Returns public sign-in options and edition-aware capability messaging.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AuthOptionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthOptions(CancellationToken ct = default)
    {
        var publicBaseUrl = PublicApplicationUrlResolver.GetApplicationBaseUri(this.Request, configuration)
            .AbsoluteUri
            .TrimEnd('/');

        if (licensingCapabilityService is null)
        {
            return this.Ok(new AuthOptionsDto(InstallationEdition.Community, ["password"], [], publicBaseUrl));
        }

        var options = await licensingCapabilityService.GetAuthOptionsAsync(ct);
        return this.Ok(options with { PublicBaseUrl = publicBaseUrl });
    }
}
