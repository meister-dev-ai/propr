// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Handles username/password login and JWT refresh.</summary>
[ApiController]
public sealed class AuthController(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHashService passwordHashService,
    IJwtTokenService jwtTokenService) : ControllerBase
{
    /// <summary>Authenticate with username and password; returns a JWT access token and refresh token.</summary>
    [HttpPost("/auth/login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return this.BadRequest(new { error = "Username and password are required." });
        }

        var user = await userRepository.GetByUsernameAsync(request.Username, ct);
        if (user is null || !user.IsActive || !passwordHashService.Verify(request.Password, user.PasswordHash))
        {
            return this.Unauthorized(new { error = "Invalid credentials." });
        }

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

        return this.Ok(new
        {
            accessToken,
            refreshToken = rawRefreshToken,
            expiresIn = 900,
            tokenType = "Bearer",
        });
    }

    /// <summary>Exchange a valid refresh token for a new JWT access token.</summary>
    [HttpPost("/auth/refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return this.BadRequest(new { error = "refreshToken is required." });
        }

        var tokenHash = ComputeSha256(request.RefreshToken);
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
        return this.Ok(new
        {
            accessToken,
            expiresIn = 900,
            tokenType = "Bearer",
        });
    }

    /// <summary>Returns the current user's global role and per-client roles. Requires authentication.</summary>
    [HttpGet("/auth/me")]
    public IActionResult GetMe()
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        var clientRoles = AuthHelpers.GetClientRoles(this.HttpContext);
        var globalRole = isAdmin ? "Admin" : "User";

        return this.Ok(new
        {
            globalRole,
            clientRoles = clientRoles.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => (int)kvp.Value),
        });
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
