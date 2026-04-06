// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Personal access token management for the authenticated user.</summary>
[ApiController]
public sealed class UserPatsController(IUserPatRepository userPatRepository) : ControllerBase
{
    /// <summary>Generates a new PAT for the current user. Returns the plaintext token once.</summary>
    [HttpPost("/users/me/pats")]
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

        var rawToken = "mpr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawToken);

        var pat = new UserPat
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            TokenHash = tokenHash,
            Label = request.Label,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userPatRepository.AddAsync(pat, ct);

        return this.Ok(new
        {
            id = pat.Id,
            label = pat.Label,
            token = rawToken,
            expiresAt = pat.ExpiresAt,
            createdAt = pat.CreatedAt,
        });
    }

    /// <summary>Lists PATs for the current user (hashes are never returned).</summary>
    [HttpGet("/users/me/pats")]
    public async Task<IActionResult> ListPats(CancellationToken ct)
    {
        var userId = this.GetCurrentUserId();
        if (userId is null)
        {
            return this.Unauthorized();
        }

        var pats = await userPatRepository.ListForUserAsync(userId.Value, ct);
        return this.Ok(pats.Select(p => new
        {
            id = p.Id,
            label = p.Label,
            expiresAt = p.ExpiresAt,
            createdAt = p.CreatedAt,
            lastUsedAt = p.LastUsedAt,
            isRevoked = p.IsRevoked,
        }));
    }

    /// <summary>Revokes a PAT by id.</summary>
    [HttpDelete("/users/me/pats/{id:guid}")]
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
        var claim = this.HttpContext.Items["UserId"]?.ToString() ??
                    this.User.FindFirst("sub")?.Value;

        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

/// <summary>Create-PAT request.</summary>
public sealed record CreatePatRequest(string Label, DateTimeOffset? ExpiresAt);
