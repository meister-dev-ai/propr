// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.IdentityAndAccess;
using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeisterProPR.Api.Controllers;

/// <summary>Handles username/password login and JWT refresh.</summary>
[ApiController]
[Route("auth")]
public sealed class AuthController(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHashService passwordHashService,
    IJwtTokenService jwtTokenService,
    IAccountLockoutService accountLockoutService,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    /// <summary>Authenticate with username and password; returns a JWT access token and refresh token.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return this.BadRequest(new { error = "Username and password are required." });
        }

        var user = await userRepository.GetByUsernameAsync(request.Username, ct);
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return this.Unauthorized(new { error = "Invalid credentials." });
        }

        if (accountLockoutService.IsLockedOut(user))
        {
            return this.Unauthorized(
                new
                {
                    error = "Account is temporarily locked due to repeated failed sign-in attempts. Try again later.",
                    code = "account_locked",
                });
        }

        if (!passwordHashService.Verify(request.Password, user.PasswordHash))
        {
            await accountLockoutService.RecordFailureAsync(user, ct);
            return this.Unauthorized(new { error = "Invalid credentials." });
        }

        await accountLockoutService.ResetAsync(user, ct);

        var accessToken = jwtTokenService.GenerateAccessToken(user);

        var rawRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = ComputeSha256(rawRefreshToken);

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await refreshTokenRepository.AddAsync(refreshToken, ct);

        // Deliver the refresh token only as an httpOnly cookie (never to JS) so the session
        // survives across tabs and reloads without XSS exposure. The access token stays in the body.
        RefreshTokenCookie.Set(this.Response, rawRefreshToken, refreshToken.ExpiresAt, this.Request.IsHttps);

        return this.Ok(
            new
            {
                accessToken,
                expiresIn = 900,
                tokenType = "Bearer",
            });
    }

    /// <summary>Exchange the refresh-token cookie (or body, for legacy callers) for a new JWT access token.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request = null, CancellationToken ct = default)
    {
        var rawRefreshToken = RefreshTokenCookie.Read(this.Request) ?? request?.RefreshToken;
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return this.Unauthorized(new { error = "No refresh token present." });
        }

        var tokenHash = ComputeSha256(rawRefreshToken);
        var token = await refreshTokenRepository.GetActiveByHashAsync(tokenHash, ct);

        if (token is null)
        {
            return this.Unauthorized(new { error = "Refresh token is invalid or expired." });
        }

        var user = await userRepository.GetByIdAsync(token.UserId, ct);
        if (user is null || !user.IsActive)
        {
            return this.Unauthorized(new { error = "User account is not active." });
        }

        var accessToken = jwtTokenService.GenerateAccessToken(user);
        return this.Ok(
            new
            {
                accessToken,
                expiresIn = 900,
                tokenType = "Bearer",
            });
    }

    /// <summary>Revokes the caller's refresh tokens and clears the session cookie.</summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct = default)
    {
        var rawRefreshToken = RefreshTokenCookie.Read(this.Request);
        if (!string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            var token = await refreshTokenRepository.GetActiveByHashAsync(ComputeSha256(rawRefreshToken), ct);
            if (token is not null)
            {
                await refreshTokenRepository.RevokeAllForUserAsync(token.UserId, ct);
            }
        }

        RefreshTokenCookie.Clear(this.Response, this.Request.IsHttps);
        return this.NoContent();
    }

    /// <summary>Returns the current user's global role and per-client roles. Requires authentication.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(AuthenticatedSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticatedSessionDto>> GetMe(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is ObjectResult objectResult)
        {
            return this.StatusCode(
                objectResult.StatusCode ?? StatusCodes.Status401Unauthorized,
                objectResult.Value);
        }

        if (auth is StatusCodeResult statusCodeResult)
        {
            return this.StatusCode(statusCodeResult.StatusCode);
        }

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        var clientRoles = AuthHelpers.GetClientRoles(this.HttpContext);
        var tenantRoles = AuthHelpers.GetTenantRoles(this.HttpContext);
        var globalRole = isAdmin ? "Admin" : "User";
        var userId = AuthHelpers.GetUserId(this.HttpContext);
        var user = userId.HasValue ? await userRepository.GetByIdAsync(userId.Value, ct) : null;
        var hasLocalPassword = user is { IsActive: true, PasswordHash.Length: > 0 };
        var licensingSummary = licensingCapabilityService is null
            ? CreateCommunityFallbackSummary()
            : await licensingCapabilityService.GetSummaryAsync(ct);

        return new AuthenticatedSessionDto(
            globalRole,
            clientRoles.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => (int)kvp.Value),
            tenantRoles.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => (int)kvp.Value),
            hasLocalPassword,
            licensingSummary.Edition,
            licensingSummary.Capabilities);
    }

    private static LicensingSummaryDto CreateCommunityFallbackSummary()
    {
        return new LicensingSummaryDto(InstallationEdition.Community, null, []);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>Login request payload.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Refresh token request payload.</summary>
public sealed record RefreshRequest(string RefreshToken);
