// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Personal access token management for the authenticated user.</summary>
[ApiController]
[Route("users/me/pats")]
[TenantData(TenantDataAccess.None)]
public sealed class UserPatsController(IUserPatRepository userPatRepository) : ControllerBase
{
    /// <summary>Creates a token with the earlier of the requested expiry and the current user's lifetime limit.</summary>
    /// <param name="request">The token label and optional expiry. An omitted expiry uses the user's lifetime limit.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The token and its effective expiry. The plaintext token is returned once.</response>
    /// <response code="400">The label is missing or the requested expiry is not in the future.</response>
    /// <response code="401">The request is not authenticated.</response>
    [HttpPost]
    public async Task<IActionResult> CreatePat([FromBody] CreatePatRequest request, CancellationToken ct)
    {
        var userId = this.GetCurrentUserId();
        if (userId is null)
        {
            return this.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return this.BadRequest(new { error = "Label is required." });
        }

        UserPat pat;
        string rawToken;
        try
        {
            (pat, rawToken) = PersonalAccessTokens.Issue(userId.Value, request.Label, request.ExpiresAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return this.BadRequest(new { error = "Expiry must be in the future." });
        }

        await userPatRepository.AddAsync(pat, ct);

        return this.Ok(
            new
            {
                id = pat.Id,
                label = pat.Label,
                token = rawToken,
                expiresAt = pat.ExpiresAt,
                createdAt = pat.CreatedAt,
            });
    }

    /// <summary>Lists active tokens for the current user with their effective expiry and without their hashes.</summary>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The active tokens, including any installation lifetime limit in their expiry.</response>
    /// <response code="401">The request is not authenticated.</response>
    [HttpGet]
    public async Task<IActionResult> ListPats(CancellationToken ct)
    {
        var userId = this.GetCurrentUserId();
        if (userId is null)
        {
            return this.Unauthorized();
        }

        var pats = await userPatRepository.ListForUserAsync(userId.Value, ct);
        return this.Ok(
            pats.Select(p => new
            {
                id = p.Id,
                label = p.Label,
                expiresAt = p.ExpiresAt,
                createdAt = p.CreatedAt,
                lastUsedAt = p.LastUsedAt,
                isRevoked = p.IsRevoked,
            }));
    }

    /// <summary>Returns the maximum lifetime of tokens created by the current user.</summary>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The lifetime limit, or null when no limit applies to the user.</response>
    /// <response code="401">The request is not authenticated.</response>
    [HttpGet("policy")]
    [ProducesResponseType(typeof(PatPolicyResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var userId = this.GetCurrentUserId();
        if (userId is null)
        {
            return this.Unauthorized();
        }

        return this.Ok(new PatPolicyResponse(await userPatRepository.GetMaximumLifetimeDaysAsync(userId.Value, ct)));
    }

    /// <summary>Revokes a PAT by id.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RevokePat(Guid id, CancellationToken ct)
    {
        var userId = this.GetCurrentUserId();
        if (userId is null)
        {
            return this.Unauthorized();
        }

        await userPatRepository.RevokeAsync(id, userId.Value, ct);
        return this.NoContent();
    }

    private Guid? GetCurrentUserId()
    {
        return AuthHelpers.GetUserId(this.HttpContext);
    }
}

/// <summary>Create-PAT request.</summary>
public sealed record CreatePatRequest(string Label, DateTimeOffset? ExpiresAt);

/// <summary>The maximum token lifetime that applies to the authenticated user.</summary>
/// <param name="MaximumLifetimeDays">The maximum number of days after creation, or null when no limit applies.</param>
public sealed record PatPolicyResponse(int? MaximumLifetimeDays);
