// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoints for the selectable review aggressiveness profile catalog and per-client defaults.</summary>
[ApiController]
[Route("admin")]
public sealed class AdminReviewProfilesController(IClientAdminService clientAdminService) : ControllerBase
{
    private static ReviewProfileCatalogItemResponse ToReviewProfileCatalogItemResponse(ReviewPipelineProfile profile)
    {
        return new ReviewProfileCatalogItemResponse(profile.ProfileId, profile.DisplayName, profile.IsBaseline);
    }

    private static ClientReviewProfileResponse ToClientReviewProfileResponse(Guid clientId, ClientDto client)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(client.DefaultReviewPipelineProfileId)
            ? ReviewPipelineProfileCatalog.FileByFileBalancedProfileId
            : client.DefaultReviewPipelineProfileId;
        var source = string.IsNullOrWhiteSpace(client.DefaultReviewPipelineProfileId)
            ? ReviewProfileSource.SystemDefault
            : ReviewProfileSource.ClientDefault;

        return new ClientReviewProfileResponse(
            clientId,
            effectiveProfileId,
            source,
            client.DefaultReviewPipelineProfileUpdatedAtUtc);
    }

    /// <summary>Lists the selectable file-by-file review aggressiveness profiles.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Published review profiles returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks administrator access.</response>
    [HttpGet("review-profiles")]
    [ProducesResponseType(typeof(ReviewProfileCatalogResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetReviewProfiles(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAnyClientRole(this.HttpContext, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var profiles = await clientAdminService.GetSelectableReviewPipelineProfilesAsync(ct);
        return this.Ok(new ReviewProfileCatalogResponse(profiles.Select(ToReviewProfileCatalogItemResponse).ToList()));
    }

    /// <summary>Gets the current default review aggressiveness profile for one client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client review profile returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks administrator access to the client.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}/review-profile")]
    [ProducesResponseType(typeof(ClientReviewProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClientReviewProfile(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var client = await clientAdminService.GetByIdAsync(clientId, ct);
        return client is null ? this.NotFound() : this.Ok(ToClientReviewProfileResponse(clientId, client));
    }

    /// <summary>Sets or clears the default review aggressiveness profile for one client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Requested review profile selection; <see langword="null" /> clears the client override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client review profile updated.</response>
    /// <response code="400">Unknown profile id.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks administrator access to the client.</response>
    /// <response code="404">Client not found.</response>
    [HttpPut("clients/{clientId:guid}/review-profile")]
    [ProducesResponseType(typeof(ClientReviewProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutClientReviewProfile(
        Guid clientId,
        [FromBody] PutClientReviewProfileRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var profiles = await clientAdminService.GetSelectableReviewPipelineProfilesAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.DefaultReviewPipelineProfileId)
            && profiles.All(profile => !string.Equals(profile.ProfileId, request.DefaultReviewPipelineProfileId, StringComparison.Ordinal)))
        {
            this.ModelState.AddModelError(
                nameof(request.DefaultReviewPipelineProfileId),
                "DefaultReviewPipelineProfileId must reference a published file-by-file review profile.");
            return this.ValidationProblem();
        }

        var client = await clientAdminService.PatchAsync(
            clientId,
            null,
            null,
            defaultReviewPipelineProfileId: request.DefaultReviewPipelineProfileId ?? string.Empty,
            ct: ct);

        return client is null ? this.NotFound() : this.Ok(ToClientReviewProfileResponse(clientId, client));
    }
}

/// <summary>Catalog response for selectable client review profiles.</summary>
public sealed record ReviewProfileCatalogResponse(IReadOnlyList<ReviewProfileCatalogItemResponse> Profiles);

/// <summary>One selectable review profile entry.</summary>
public sealed record ReviewProfileCatalogItemResponse(string ProfileId, string DisplayName, bool IsDefault);

/// <summary>Client-scoped review profile response.</summary>
public sealed record ClientReviewProfileResponse(
    Guid ClientId,
    string DefaultReviewPipelineProfileId,
    ReviewProfileSource Source,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>Request body for setting a client's default review profile.</summary>
public sealed record PutClientReviewProfileRequest(string? DefaultReviewPipelineProfileId);

/// <summary>Indicates whether the effective review profile comes from the client or the system default.</summary>
public enum ReviewProfileSource
{
    SystemDefault,
    ClientDefault,
}
