// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Resolves Azure DevOps identities for use in crawl configuration.</summary>
[ApiController]
public sealed class IdentitiesController(IIdentityResolver identityResolver) : ControllerBase
{
    /// <summary>
    ///     Resolves ADO identities matching the given display name within an organisation.
    ///     Returns the VSS identity GUID required for the client's <c>reviewerId</c> setting.
    ///     Works for users, service principals, and managed identities.
    ///     Requires either a global admin or a user with <c>ClientAdministrator</c> access for at least one client,
    ///     authenticated via JWT bearer token or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="orgUrl">Azure DevOps organisation URL (e.g. <c>https://dev.azure.com/my-org</c>).</param>
    /// <param name="displayName">Display name of the identity to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One or more matching identities.</response>
    /// <response code="400">Missing required query parameters.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is authenticated but lacks <c>ClientAdministrator</c> access.</response>
    /// <response code="404">No identity found with that display name.</response>
    [HttpGet("identities/resolve")]
    [ProducesResponseType(typeof(IReadOnlyList<IdentityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResolveIdentity(
        [FromQuery] string? orgUrl,
        [FromQuery] string? displayName,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAnyClientRole(this.HttpContext, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(orgUrl))
        {
            return this.BadRequest(new { error = "orgUrl is required." });
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return this.BadRequest(new { error = "displayName is required." });
        }

        var matches = await identityResolver.ResolveAsync(orgUrl, displayName, Guid.Empty, ct);

        if (matches.Count == 0)
        {
            return this.NotFound(new { error = $"No identity found with display name '{displayName}'." });
        }

        return this.Ok(matches.Select(m => new IdentityResponse(m.Id, m.DisplayName)).ToList());
    }

    /// <summary>Resolved ADO identity.</summary>
    /// <param name="Id">VSS identity GUID — use as <c>reviewerId</c> when setting the client reviewer identity.</param>
    /// <param name="DisplayName">Human-readable display name.</param>
    public sealed record IdentityResponse(Guid Id, string DisplayName);
}
