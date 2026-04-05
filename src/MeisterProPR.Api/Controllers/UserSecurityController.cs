// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Authenticated user security endpoints.</summary>
[ApiController]
public sealed class UserSecurityController(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHashService passwordHashService) : ControllerBase
{
    /// <summary>Changes the current user's password and revokes all refresh tokens.</summary>
    /// <param name="request">Current and new password payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Password changed successfully.</response>
    /// <response code="400">The request is invalid or the new password does not meet policy.</response>
    /// <response code="401">Authentication is missing, invalid, or the current password is incorrect.</response>
    [HttpPost("/users/me/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (!userId.HasValue)
        {
            return this.Unauthorized(new { error = "Valid credentials required." });
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return this.BadRequest(new { error = "currentPassword and newPassword are required." });
        }

        if (request.NewPassword.Length < 8)
        {
            return this.BadRequest(new { error = "Password must be at least 8 characters." });
        }

        var user = await userRepository.GetByIdAsync(userId.Value, ct);
        if (user is null || !user.IsActive)
        {
            return this.Unauthorized(new { error = "Invalid credentials." });
        }

        if (!passwordHashService.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return this.Unauthorized(new { error = "Current password is incorrect." });
        }

        var newPasswordHash = passwordHashService.Hash(request.NewPassword);
        await userRepository.UpdatePasswordHashAsync(user.Id, newPasswordHash, ct);
        await refreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);

        return this.NoContent();
    }
}

/// <summary>Password change request payload.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
