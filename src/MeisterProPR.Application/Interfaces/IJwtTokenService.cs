using System.Security.Claims;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Issues and validates short-lived JWT access tokens.</summary>
public interface IJwtTokenService
{
    /// <summary>Generates a 15-minute JWT access token for the given user.</summary>
    string GenerateAccessToken(AppUser user);

    /// <summary>
    ///     Validates an access token and returns the associated <see cref="ClaimsPrincipal"/>,
    ///     or <see langword="null"/> if the token is invalid or expired.
    /// </summary>
    ClaimsPrincipal? ValidateAccessToken(string token);
}
